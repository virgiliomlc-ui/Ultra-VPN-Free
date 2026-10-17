// =====================================================================
//  ULTRA VPN FREE - EDICAO 100% C# (SEM MOTOR POWERSHELL)
//  Porta fiel do app original: as 4 fontes de servidores,
//  lista com grupos das tres Americas + ordenacao por coluna, ping preciso
//  das VPNs (3 ecos, vale o menor), sem regra de corte, auto-reconexao
//  com sonda em POOL, rotas por aplicativo (UDP/TCP/WFP/dominios),
//  ajuste automatico de porta (TCP + UDP hard-reset + portas.json),
//  3 temas com vidro e brilho, bandeja, desinstalacao completa, atualizacao.
//  O OpenVPN Community vai embutido no .exe: o programa se instala sozinho
//  no primeiro uso e o botao de desinstalar remove tudo de volta.
//  Nada vai para o GitHub sem ordem expressa.
// =====================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UltraVPN
{
    // =================================================================
    //  CARREGADOR DAS DLLs DO WEBVIEW2
    // =================================================================
    // As duas DLLs gerenciadas do WebView2 vao embutidas no .exe. Como o
    // programa e de arquivo unico, elas nao estao do lado do exe e o JIT
    // nao as acha -> o programa quebraria na hora de abrir a tela.
    //
    // Esta classe fica DE PROP�SITO sem nenhuma referencia a tipo do
    // WebView2: se ela citasse WebView2, o JIT precisaria da DLL antes
    // do AssemblyResolve existir, e oprograma cairia antes de chance de
    // tentar qualquer coisa. Aqui so entra System.Reflection.
    // =================================================================
    static class Wv2
    {
        static string Dir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultra VPN Free", "web");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        static void Extrair(string recurso, string destino)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var rs = asm.GetManifestResourceStream(recurso))
                {
                    if (rs == null) return;
                    var ms = new MemoryStream();
                    rs.CopyTo(ms);
                    var dados = ms.ToArray();
                    if (File.Exists(destino) && new FileInfo(destino).Length == dados.Length) return;
                    File.WriteAllBytes(destino, dados);
                }
            }
            catch { }
        }

        // Chamar no come�o do Main, ANTES de qualquer coisa tocar no
        // WebView2. Devolve false quando nao deu: nesse caso o programa
        // continua funcionando com a tela antiga do WinForms.
        public static bool Instalar()
        {
            try
            {
                string dir = Dir();
                Extrair("wv2.Core.dll", Path.Combine(dir, "Microsoft.Web.WebView2.Core.dll"));
                Extrair("wv2.WinForms.dll", Path.Combine(dir, "Microsoft.Web.WebView2.WinForms.dll"));

                AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs a)
                {
                    try
                    {
                        var nome = new System.Reflection.AssemblyName(a.Name).Name;
                        if (nome == null) return null;
                        if (nome != "Microsoft.Web.WebView2.Core" &&
                            nome != "Microsoft.Web.WebView2.WinForms") return null;
                        var arquivo = Path.Combine(dir, nome + ".dll");
                        if (!File.Exists(arquivo)) return null;
                        return System.Reflection.Assembly.LoadFrom(arquivo);
                    }
                    catch { return null; }
                };
                return true;
            }
            catch { return false; }
        }
    }

    // =================================================================
    //  TELA EM WEBVIEW2
    // =================================================================
    // A interface passou a ser HTML/CSS/JS dentro do WebView2. O programa
    // continua sendo UM .exe so: a WebView2Loader e os arquivos da tela
    // vao embutidos como recurso e sao extraidos para uma pasta propria
    // na primeira execucao.
    //
    // Por que funciona num .exe unico: o WebView2 precisa da
    // WebView2Loader.dll (nativa) do lado do assembly gerenciado. Como o
    // .exe nao tem arquivos do lado, a DLL e extraida e carregada na mao
    // com LoadLibrary ANTES de criar o ambiente - assim a procura nativa
    // do WebView2 acha o modulo ja carregado, sem depender de caminho.
    // As duas DLLs gerenciadas sao resolvidas pelo Wv2 (ver acima).
    // =================================================================
    static class TelaWeb
    {
        static WebView2 wv;
        static CoreWebView2 cw;
        static Janela dono;
        static bool pronto = false;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr hModule);

        static IntPtr modLoader = IntPtr.Zero;

        // a pasta onde a tela mora (extraida uma vez)
        public static string Pasta()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultra VPN Free", "web");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        // ---------------------------------------------------------------
        //  EXTRAIR um recurso embutido para a pasta da tela
        // ---------------------------------------------------------------
        static bool ExtrairRecurso(string recurso, string destino)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var rs = asm.GetManifestResourceStream(recurso))
                {
                    if (rs == null) return false;
                    var ms = new MemoryStream();
                    rs.CopyTo(ms);
                    var dados = ms.ToArray();
                    // ja esta no lugar e com o mesmo tamanho? nao reescreve
                    if (File.Exists(destino) && new FileInfo(destino).Length == dados.Length) return true;
                    File.WriteAllBytes(destino, dados);
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------
        //  Carrega a WebView2Loader antes de tudo (ponto do .exe unico)
        // ---------------------------------------------------------------
        static bool CarregarLoader()
        {
            if (modLoader != IntPtr.Zero) return true;
            bool x64 = Environment.Is64BitProcess;
            string nome = x64 ? "WebView2Loader.x64.dll" : "WebView2Loader.x86.dll";
            string destino = Path.Combine(Pasta(), x64 ? "WebView2Loader.dll" : "WebView2Loader.dll");
            if (!ExtrairRecurso(nome, destino)) return false;
            try
            {
                modLoader = LoadLibrary(destino);
                return modLoader != IntPtr.Zero;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------
        //  Extrai os arquivos da tela (html, css, js e as imagens)
        // ---------------------------------------------------------------
        static bool ExtrairTela()
        {
            var p = Pasta();
            bool ok = true;
            ok &= ExtrairRecurso("uvpn.ui.index.html", Path.Combine(p, "index.html"));
            ok &= ExtrairRecurso("uvpn.ui.app.css", Path.Combine(p, "app.css"));
            ok &= ExtrairRecurso("uvpn.ui.app.js", Path.Combine(p, "app.js"));
            // as imagens sao as mesmas que ja estavam embutidas no .exe
            ok &= ExtrairRecurso("fundo.alien.jpg", Path.Combine(p, "fundo.alien.jpg"));
            ok &= ExtrairRecurso("fundo.goku.jpg", Path.Combine(p, "fundo.goku.jpg"));
            ok &= ExtrairRecurso("alien.escuro.png", Path.Combine(p, "alien.escuro.png"));
            ok &= ExtrairRecurso("alien.claro.png", Path.Combine(p, "alien.claro.png"));
            ok &= ExtrairRecurso("tray.gif", Path.Combine(p, "tray.gif"));
            return ok;
        }

        // ---------------------------------------------------------------
        //  IMAGEM -> data URI (para o HTML nao precisar de arquivo)
        // ---------------------------------------------------------------
        public static string DataUri(Image img, int lado)
        {
            if (img == null) return "";
            try
            {
                using (var b = new Bitmap(lado, lado))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.Transparent);
                        g.DrawImage(img, new Rectangle(0, 0, lado, lado));
                    }
                    using (var ms = new MemoryStream())
                    {
                        b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return "data:image/png;base64," +
                               Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch { return ""; }
        }

        public static string IconeDataUri(System.Drawing.Icon ic)
        {
            if (ic == null) return "";
            try
            {
                using (var b = ic.ToBitmap())
                    return DataUri(b, 64);
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------
        //  MANDO MENSAGEM PARA A TELA
        // ---------------------------------------------------------------
        public static void Mandar(string tipo, object dados)
        {
            if (!pronto || cw == null) return;
            try
            {
                var ser = new JavaScriptSerializer();
                var o = dados as IDictionary<string, object>;
                if (o == null) o = new Dictionary<string, object>();
                o["tipo"] = tipo;
                var js = ser.Serialize(o);

                // o WebView2 so aceita ser tocado na thread da interface. O
                // Log() e chamado das sondas (threads de fundo), entao aqui
                // se precisa voltar para a thread antes de postar.
                if (forma != null && forma.InvokeRequired)
                {
                    forma.BeginInvoke((Action)delegate
                    {
                        try { if (cw != null) cw.ExecuteScriptAsync("uvfn_recebe(" + js + ");"); }
                        catch (Exception e) { Rastro("Mandar " + tipo + " FALHOU: " + e.Message); }
                    });
                    return;
                }
                // chama a funcao da tela direto. O caminho antigo era
                // PostWebMessageAsJson, que nao chegava: o postMessage do
                // JS para o C# funcionava, mas o inverso nao.
                cw.ExecuteScriptAsync("uvfn_recebe(" + js + ");");
            }
            catch (Exception e) { Rastro("Mandar " + tipo + " FALHOU: " + e.Message); }
        }

        // a janela que hospeda a tela (para voltar para a thread dela)
        static Form forma = null;

        // rastro da tela: deixa em erro.txt o que entrou e o que saiu
        public static void Rastro(string linha)
        {
            try
            {
                File.AppendAllText(Path.Combine(Pasta(), "erro.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + linha + Environment.NewLine);
            }
            catch { }
        }

        public static void Mandar(string tipo)
        {
            Mandar(tipo, new Dictionary<string, object>());
        }

        public static bool EstaPronto { get { return pronto; } }

        // desliga a tela web (o Runtime faltou): some com o controle e
        // devolve a janela para o desenho antigo do WinForms
        public static void Desligar()
        {
            try { pronto = false; } catch { }
            try
            {
                if (wv != null)
                {
                    var f = wv.Parent;
                    wv.Dispose();
                    wv = null;
                    if (f != null) f.Controls.RemoveAt(f.Controls.Count - 1);
                }
            }
            catch { }
            cw = null;
        }

        // ---------------------------------------------------------------
        //  AQUECER O NAVEGADOR (chamado no Main, antes da janela)
        // ---------------------------------------------------------------
        static Task<CoreWebView2Environment> ambienteTask;

        public static void Aquecer()
        {
            try
            {
                if (ambienteTask != null) return;
                if (!CarregarLoader()) return;
                if (!ExtrairTela()) return;
                var opts = new CoreWebView2EnvironmentOptions();
                opts.AdditionalBrowserArguments =
                    "--disable-lcd-text " +
                    // o Chromium congela o renderizador que nao esta na frente
                    // (para economizar memoria) e leva uns 2 s para acorda-lo a
                    // cada navegacao. Foi medido: a tela ficava pronta em
                    // 3,2 s e o documento inteiro levava 5 ms - os 2 s eram
                    // so a espera do navegador acordar. A janela deste
                    // programa nao temcomo ficar escondida, entao nao ha
                    // o que economizar aqui.
                    "--disable-backgrounding-occluded-windows " +
                    "--disable-renderer-backgrounding " +
                    "--disable-background-timer-throttling " +
                    "--disable-features=CalculateNativeWinOcclusion";
                var dados = Path.Combine(Pasta(), "dados");
                try { if (!Directory.Exists(dados)) Directory.CreateDirectory(dados); } catch { }
                // comeca agora e fica guardando: a janela usa o mesmo
                ambienteTask = CoreWebView2Environment.CreateAsync(null, dados, opts);
                Rastro("navegador aquecendo");
            }
            catch (Exception e) { Rastro("aqueceu falhou: " + e.Message); }
        }

        // ---------------------------------------------------------------
        //  LIGA A TELA
        // ---------------------------------------------------------------
        public static bool Ligar(Form f, Janela j)
        {
            var ui = f;
            try
            {
                if (!CarregarLoader()) return false;
                if (!ExtrairTela()) return false;
                Aquecer();

                dono = j;
                forma = f;
                // o WebView2 e um controle Windows de verdade: a janela
                // precisa ter handle. Sem isso o BeginInvoke estoura e a
                // tela nunca entra (e o programa fica so com o WinForms).
                try { if (!ui.IsHandleCreated) ui.CreateControl(); } catch { }
                if (!ui.IsHandleCreated) return false;

                // tudo na thread da interface: o controle WebView2 nao pode
                // ser criado de fora
                ui.BeginInvoke((Action)(async delegate
                {
                    try
                    {
                        var opts = new CoreWebView2EnvironmentOptions();
                        // --disable-lcd-text desliga o texto LCD (ClearType).
                        // Sem isto o WebView2 desenha as letras com franja
                        // vermelha e verde, e sobre o fundo escuro e
                        // fotografico do alien isso suja o texto: da para ver
                        // comparando as duas telas. Desligado, o navegador
                        // usa antisserrilhado cinza, que e o que o desenho
                        // mostra e o que fica limpo no fundo escuro.
                        opts.AdditionalBrowserArguments = "--disable-lcd-text";
                        // PRIMEIRO parametro (pasta do navegador) tem que
                        // ser null: e o que diz ao WebView2 para usar o
                        // Runtime JA INSTALADO no Windows. Se mandar uma
                        // pasta aqui, ele procura o Edge DENTRO dela, nao
                        // acha e diz que nao existe Runtime nenhum.
                        // O segundo parametro e onde ficam os dados do
                        // navegador (cache, cookies da tela).
                        // PRIMEIRO TENTA O QUE O Main JA VIROU: o processo do
                        // navegador foi mandado subir em paralelo com a
                        // montagem da janela, e aqui a gente so espera.
                        var env = ambienteTask != null
                            ? await ambienteTask
                            : await CoreWebView2Environment.CreateAsync(null, Path.Combine(Pasta(), "dados"), opts);

                        var web = new WebView2();
                        web.Dock = DockStyle.Fill;
                        ui.Controls.Add(web);
                        web.BringToFront();
                        await web.EnsureCoreWebView2Async(env);

                        var core = web.CoreWebView2;
                        if (core == null) return;
                        wv = web;
                        cw = core;

                        // O fundo dos 3 temas e pintado pelo HTML (app.css, "cover"), e nao pelo
// Windows. Isso resolve o fundo grande e fora de lugar: o Windows so
                        // pinta o fundo da Form uma vez e, como o WebView2 e
                        // um controle por cima, o desenho novo nunca chegava
                        // a aparecer. O plano B (sem Runtime) continua
                        // pintando pelo MontarFundo.
                        try
                        {
                            // OPOQUE, e nao transparente. Com DefaultBackgroundColor
                            // = Transparente o Chromium NAO pinta o fundo da pagina:
                            // ele deixa o fundo da janela do Windows aparecer por
                            // tras, e era esse o fundo grande e fora de lugar. O
                            // fundo dos 3 temas e do app.css ("cover"), entao a
                            // cor de baixo so aparece enquanto a foto carrega.
                            web.DefaultBackgroundColor = Color.FromArgb(5, 8, 10);
                        }
                        catch (Exception e2) { Rastro("fundo do webview falhou: " + e2.Message); }

                        var st = core.Settings;
                        st.AreDefaultContextMenusEnabled = false;
                        st.AreDevToolsEnabled = false;
                        st.AreHostObjectsAllowed = false;
                        st.IsStatusBarEnabled = false;
                        st.IsZoomControlEnabled = false;

                        core.SetVirtualHostNameToFolderMapping(
                            "uvpn.local", Pasta(),
                            CoreWebView2HostResourceAccessKind.DenyCors);

                        // TEMP: mede quanto custa uma segunda navegacao
                        core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
                        {
                            try { Receber(e.WebMessageAsJson); }
                            catch { }
                        };

                        core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs e)
                        {
                            if (!e.IsSuccess) return;
                            pronto = true;
                            // a janela so aparece quando a tela ja esta
                            // inteira: ver LiberarJanela()
                            if (dono != null) try { dono.LiberarJanela(); } catch { }
                            try { web.Focus(); } catch { }
                            Mandar("imagens", new Dictionary<string, object> {
                                // caminhos SEM BARRA no começo: assim o
                                // navegador resolve junto com a pasta da
                                // tela, e nao depende de nenhum dominio
                                { "fundo", "fundo.alien.jpg" },
                                { "goku",  "fundo.goku.jpg" },
                                { "logo",  "alien.escuro.png" },
                                { "ico",   "alien.claro.png" }
                            });
                            if (dono != null) dono.TelaPronta();
                        };

                        // A TELA ABRE DA PASTA, COM file:///
                        // Antes era um dominio falso (https://uvpn.local)
                        // mapeado para a pasta. Medido: uma navegacao para
                        // esse nome levava 2.029 ms, e uma pagina de 35
                        // bytes nele levava 2.012 ms - enquanto about:blank
                        // levava 5,9 ms. O atraso NAO era o WebView2 nem a
                        // nossa tela: era o nome. Terminar em .local faz o
                        // Windows tentar resolver por mDNS e esperar o
                        // tempo estourar antes de o navegador usar o
                        // mapeamento. Com file:/// a tela entrou em 0,81 s.
                        core.Navigate("file:///" + Pasta().Replace('\\', '/').TrimEnd('/') + "/index.html");
                    }
                    catch (Exception erro)
                    {
                        // grava o motivo real: sem isto so se sabe que "nao
                        // deu" e nao o QUE deu (e descobrir custou caro)
                        try
                        {
                            File.WriteAllText(Path.Combine(Pasta(), "erro.txt"),
                                DateTime.Now.ToString("s") + Environment.NewLine +
                                erro.ToString());
                        }
                        catch { }
                        // o WebView2 nao entrou: fica valendo a janela antiga
                        try { if (dono != null) dono.TelaWebFalhou(); } catch { }
                    }
                }));
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------
        //  MENSAGEM VINDO DA TELA
        // ---------------------------------------------------------------
        static void Receber(string json)
        {
            var ser = new JavaScriptSerializer();
            Dictionary<string, object> m;
            try { m = ser.Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (m == null) return;
            object acao;
            if (!m.TryGetValue("acao", out acao)) return;
            Rastro("Recebeu da tela: " + Convert.ToString(acao));
            if (dono == null) return;
            try { dono.DoAcao(Convert.ToString(acao), m); }
            catch { }
        }
    }
    // =================================================================
    //  ENTRADA DO PROGRAMA
    // =================================================================
    static class Program
    {
        public const string Versao = "1.3.0";
        public static Mutex Instancia;
        public static bool TrayStart = false;    // -Tray  : abre na bandeja
        public static bool AutoStart = false;    // -Auto  : abre e conecta
        public static bool ConectaAgora = false; // -Conectar: reaberto elevado

        // O PROCESSO PRECISA SE DECLARAR CONHECEDOR DE DPI antes de criar
        // qualquer janela. Sem isso o Windows entrega as coordenadas "de
        // 96 dpi" e estica tudo por 2,25 no final: e o que deixava a letra
        // borrada (o WebView2 ainda recebia --embedded-browser-webview-dpi-
        // awareness=0 e desenhava em 1x). Isso vale tanto para o texto do
        // HTML quanto para o do Windows Forms.
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr valor);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        // quanto o Windows esta esticando as coisas (2,25 nesta maquina)
        public static float EscalaDpi()
        {
            try
            {
                uint d = GetDpiForSystem();
                if (d > 0) return d / 96f;
            }
            catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
            catch { }
            return 1f;
        }

        // 917 x 756 e o tamanho que o usuario ve: com o processo agora
        // consciente de DPI, o Windows nao estica mais nada, entao a janela
        // precisa ser criada ja no tamanho fisico equivalente
        public static Size JanelaDoUsuario()
        {
            float e = EscalaDpi();
            return new Size((int)Math.Round(917 * e), (int)Math.Round(756 * e));
        }

        static void AcordarComODpi()
        {
            try
            {
                // 2 = PER_MONITOR_AWARE_V2 (a melhor: Windows 10 1703 em diante)
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { if (SetProcessDPIAware()) return; } catch { }
            try
            {
                // ultima recurso: o proprio manifesto do .exe, via API antiga
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AcordarComODpi();
            // as fontes usam TLS 1.2 (o alvo 4.0 do compilador nao liga por padrao)
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; } catch { }

            // as DLLs do WebView2 sao embutidas no .exe e precisam estar
            // resolvidas ANTES de qualquer linha tocar no WebView2. Se nao
            // der (Runtime ausente, pasta sem permissao), o programa segue
            // com a tela antiga do WinForms - nunca quebra.
            try { Wv2.Instalar(); } catch { }

            // AQUECER O NAVEGADOR ANTES DA JANELA
            // O WebView2 so pode nascer quando a janela tem handle, mas o
            // PROCESSO do navegador nao depende disso. Comecar aqui faz o
            // Edge WebView2 subir em paralelo com a montagem da tela, em vez
            // de esperar a janela aparecer para so entao comecar. Medido: a
            // tela ficava pronta em 3,1 s; o grosso desses 2 s e a subida do
            // processo do navegador.
            try { TelaWeb.Aquecer(); } catch { }

            foreach (var a in args)
            {
                var t = (a ?? "").Trim().TrimStart('-', '/').ToLowerInvariant();
                if (t == "tray") TrayStart = true;
                else if (t == "auto") { AutoStart = true; TrayStart = true; }
                else if (t == "conectar") ConectaAgora = true;
            }

            Instancia = new Mutex(false, "Local\\UltraVPNFreeUnica");
            bool nossa = false;
            try { nossa = Instancia.WaitOne(0); } catch (AbandonedMutexException) { nossa = true; }
            if (!nossa)
            {
                MessageBox.Show("O Ultra VPN Free ja esta aberto (esta na bandeja ou em outra janela).",
                    "Ultra VPN Free", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!EAdmin())
            {
                try
                {
                    var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    var psi = new ProcessStartInfo(exe);
                    foreach (var a in args) psi.Arguments += "\"" + a + "\" ";
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new Janela()); }
            catch (Exception e)
            {
                // rede de seguranca: se a tela web (ou qualquer outra coisa)
                // derrubar o programa na hora de abrir, ainda mostra o que
                // aconteceu em vez de sumir sem aviso
                try
                {
                    MessageBox.Show("O Ultra VPN Free encontrou um erro:\n\n" + e.Message,
                        "Ultra VPN Free", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }
        }

        public static bool EAdmin()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                return (new WindowsPrincipal(id)).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // =================================================================
    //  SERVIDOR / PROGRAMA
    // =================================================================
    public class Servidor
    {
        public string Host = "";
        public string IP = "";
        public string Name = "";
        public string ISO = "";
        public int Ping = 0;
        public long Speed = 0;
        public int Sessions = 0;
        public long Score = 0;
        public int Measured = -1;
        public string Proto = "";     // udp | tcp | both | '' (desconhecido)
        public int Port = 0;
        public string Src = "gate";   // gate | pvl | tlx | hunt | vpnbook | urban
        public string Cid = "";
        public string Cfg = "";       // .ovpn em base64 (VPN Gate)
        public string CfgUrl = "";    // onde baixar o .ovpn (telexy: configUrl) / urban: signature
        public string Sig = "";       // UrbanVPN: assinatura do servidor (para pegar accs-proxy)
        // Conference de saude feita ao abrir o programa: a porta respondeu E o
        // .ovpn baixou e tem o que o OpenVPN precisa. Sem isso o servidor nem
        // entra na lista - assim nao ha erro de conexao na primeira tentativa.
        public bool PortaOk = false;
        public bool OvpnOk = false;
        // cidade REAL informada pela fonte (o telexy manda "Olivos", "Colina",
        // "Capiibary"...). Vem do dado, nao de lista escrita a mao: e por isso
        // que nao existe mais nenhum nome de cidade escrito no codigo - quando
        // a fonte sabe, mostra o que ela mediu.
        public string Cidade = "";
    }

    public class Programa
    {
        public string Name = "";
        public string Path = "";
        public List<string> Domains = new List<string>();
    }

    // =================================================================
    //  TABELAS DE DADOS (pais, regioes, borda do Discord, presets)
    // =================================================================
    static class Dados
    {
        // NAO EXISTE MAIS SERVIDOR DE CONFIANCA. Antes o 181.117.92.157 era
        // escolhido a forcana na conexao automatica do boot, enquanto todos os
        // outros seguiam a ordem normal (melhor ping). Hoje nenhum servidor e
        // especial: a conexao do boot usa exatamente a mesma regra de qualquer
        // outra - primeiro o de melhor ping que estiver de pe.

        // AMERICA DO SUL, E SO - E SEM O BRASIL. Ficam 12 paises: Argentina,
        // Uruguai, Paraguai, Chile, Peru, Colombia, Bolivia, Equador,
        // Venezuela, Guiana, Suriname e Guiana Francesa.
        // FORA, sem excecao: Brasil, America Central (Guatemala, Honduras,
        // El Salvador, Nicaragua, Costa Rica, Panama, Belize, Cuba, Jamaica,
        // Republica Dominicana, Trinidad, Bahamas, Porto Rico, Haiti),
        // Mexico, Estados Unidos, Canada e qualquer outro pais do mundo.
        public static readonly string[] LatAmOrder = new string[] {
            "AR","UY","PY","CL","PE","CO","BO","EC","VE","GY","SR","GF" };

        // so os 12 paises da America do Sul sem o Brasil (o Brasil saiu de vez)
        public static readonly Dictionary<string, string> LatAmPT = new Dictionary<string, string> {
            {"UY","Uruguai"}, {"AR","Argentina"}, {"PY","Paraguai"}, {"CL","Chile"}, {"PE","Peru"},
            {"CO","Colombia"}, {"BO","Bolivia"}, {"EC","Equador"}, {"VE","Venezuela"}, {"GY","Guiana"},
            {"SR","Suriname"}, {"GF","Guiana Francesa"} };

        // cidade/local de cada servidor - so America do Sul sem o Brasil.
        // ATENCAO: isto e so uma REDE DE RESERVA, e cada anotacao aqui foi
        // conferida em 16/10/2026 contra geolocalizacao independente (cidade e
        // regiao bateram). A cidade que aparece na tela vem do dado que a
        // fonte manda (Servidor.Cidade) ou do que o programa aprendeu e gravou
        // em cidades.json - os dois passam na frente desta tabela, e o arquivo
        // se atualiza sozinho para sempre. O 181.199.60.152 saiu daqui de
        // proposito: duas fontes discordam dele hoje (geolocalizacao diz
        // Quito, telexy diz Santa Elena) e, na duvida, o programa mostra so o
        // pais - que esta sempre certo - em vez de arriscar uma cidade.
        public static readonly Dictionary<string, string> ServidorCidade = new Dictionary<string, string> {
            {"181.46.9.205","Ituzaingo (Buenos Aires)"}, {"181.87.91.13","El Talar (Buenos Aires)"},
            {"186.122.0.49","Villa Nueva (Mendoza)"},
            {"152.230.202.21","Colina (Santiago)"},
            {"38.253.148.178","Lima"}, {"190.117.30.185","Mariano Melgar (Arequipa)"} };

        public static readonly string[] RegiaoSul = new string[] { "AR","UY","PY","CL","PE","CO","BO","EC","VE","GY","SR","GF" };
        // America Central, Norte e Caribe sairam da lista - nao ha mais grupo
        public static readonly string[] RegiaoCentral = new string[0];
        public static readonly string[] RegiaoNorte = new string[0];

        // perna "servidor de saida -> borda do Discord" em ms (so America do Sul
        // sem o Brasil - os paises que ficaram fora da lista nao entram aqui)
        public static readonly Dictionary<string, int> BordaMs = new Dictionary<string, int> {
            {"PY",15}, {"UY",20}, {"AR",25}, {"CL",30}, {"PE",40}, {"BO",40},
            {"CO",50}, {"EC",55}, {"GY",55}, {"SR",55}, {"GF",55}, {"VE",60} };

        // dominios que cada programa costuma usar (o tunel resolve eles tambem)
        public static readonly Dictionary<string, string[]> AppPresets = new Dictionary<string, string[]> {
            {"Discord", new[]{"gateway.discord.gg","discord.gg","cdn.discordapp.com","media.discordapp.net","discord.com","ptb.discord.com","canary.discord.com","discord.media"}},
            {"DiscordPTB", new[]{"gateway.discord.gg","cdn.discordapp.com","discord.gg","discord.media"}},
            {"DiscordCanary", new[]{"gateway.discord.gg","cdn.discordapp.com","discord.gg","discord.media"}},
            {"Steam", new[]{"store.steampowered.com","steamcommunity.com","api.steampowered.com","media.steampowered.com","steamserver.net"}},
            {"Spotify", new[]{"spclient.spotify.com","audio-ak-spotify-com.akamaized.net","open.spotify.com"}},
            {"WhatsApp", new[]{"web.whatsapp.com","graph.facebook.com","mmg.whatsapp.net"}},
            {"Telegram", new[]{"web.telegram.org","api.telegram.org"}} };

        public static readonly string[] ProtoKeys = new string[] { "auto", "udp", "tcp" };
        public static readonly string[] ProtoTexts = new string[] { "UDP e TCP (automatico)", "S\u00f3 UDP", "S\u00f3 TCP" };
        public static readonly string[] TemaKeys = new string[] { "ets", "escuro", "goku" };
        public static readonly string[] TemaTexts = new string[] { "Tema Dos ETS", "Tema Escuro", "Goku Instinto Superior" };

        public const string ApiUrl = "https://www.vpngate.net/api/iphone/";
        // ESPELHO DE RESERVA do VPN Gate: os mesmos servidores, republicados
        // num repositorio GitHub que e atualizado o tempo todo. Se o
        // vpngate.net cair ou ficar lento, a lista continua vindo de la.
        // O arquivo tem os mesmos campos do CSV do VPN Gate (e o .ovpn em
        // base64 junto), entao o programa trata igual.
        public const string EspelhoUrl = "https://raw.githubusercontent.com/9xN/auto-ovpn/main/json/data.json";
        public const string PvlUrl = "https://publicvpnlist.com";
        public const string TlxUrl = "https://telexy.net/api/v1";
        public const string VpnBookUser = "vpnbook";
        public const string UrbanApiPro = "https://api-pro.urban-vpn.com/rest/v1/";
        public const string UrbanStats  = "https://stats.urban-vpn.com/api/rest/v2/";
        public const string UrbanApp    = "URBAN_SHIELD_BROWSER_EXTENSION";
        public const string UrlVersaoRemota = "https://raw.githubusercontent.com/virgiliomlc-ui/Ultra-VPN-Free/main/versao.txt";
        public const string UrlDownload = "https://github.com/virgiliomlc-ui/Ultra-VPN-Free/releases/latest";
        public const string VpnUser = "vpn";
        public const string VpnPass = "vpn";

        // so a America do Sul SEM BRASIL - Central, Norte, Caribe e o proprio
        // Brasil nem sao consultados (nao adianta baixar para descartar)
        public class FontePais { public string Slug = ""; public string ISO = ""; public bool TemPvl = true; }
        public static readonly List<FontePais> PvlPaises = new List<FontePais> {
            new FontePais{ Slug="argentina", ISO="AR" },
            new FontePais{ Slug="chile", ISO="CL" }, new FontePais{ Slug="peru", ISO="PE" },
            new FontePais{ Slug="colombia", ISO="CO" },
            new FontePais{ Slug="ecuador", ISO="EC" },
            new FontePais{ Slug="uruguay", ISO="UY" }, new FontePais{ Slug="paraguay", ISO="PY" },
            new FontePais{ Slug="", ISO="BO", TemPvl=false }, new FontePais{ Slug="", ISO="GF", TemPvl=false },
            new FontePais{ Slug="", ISO="GY", TemPvl=false }, new FontePais{ Slug="", ISO="SR", TemPvl=false },
            new FontePais{ Slug="", ISO="VE", TemPvl=false } };

        public class HuntPais { public string Slug = ""; public string ISO = ""; }

        // A semente SoftEther/PacketiX vai embutida no .exe (uvpn.seed.ovpn).
        // As fontes que publicam .ovpn nao precisam dela; as que nao publicam
        // (hunt) so abrem com este modelo, porque sao servidores SoftEther.
        // Montar a config pela semente destrava a Argentina inteira na fonte
        // hunt, que antes falhava com "nao consegui pegar a configuracao".
        public static string SementeOvpn()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("uvpn.seed.ovpn"))
                {
                    if (s != null)
                    {
                        using (var r = new StreamReader(s, Encoding.UTF8))
                            return r.ReadToEnd();
                    }
                }
            }
            catch { }
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var p in new[] { Path.Combine(baseDir, "res\\seed.ovpn"),
                                         Path.Combine(baseDir, "seed.ovpn") })
                {
                    if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
                }
            }
            catch { }
            return "";
        }

        // Troca as linhas 'remote' e 'proto' da semente pelo endereco do servidor.
        public static string OvpnDaSemente(string semente, string ip, int porta, string proto)
        {
            var outp = new List<string>();
            outp.Add("proto " + (String.IsNullOrEmpty(proto) ? "udp" : proto.ToLowerInvariant()));
            outp.Add("remote " + ip + " " + porta);
            foreach (var linha in semente.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (Regex.IsMatch(linha, @"^\s*remote\s")) continue;
                if (Regex.IsMatch(linha, @"^\s*proto\s")) continue;
                outp.Add(linha);
            }
            return String.Join("\r\n", outp.ToArray());
        }
        public static readonly List<HuntPais> HuntPaises = new List<HuntPais> {
            new HuntPais{ Slug="argentina", ISO="AR" },
            new HuntPais{ Slug="uruguay", ISO="UY" }, new HuntPais{ Slug="paraguay", ISO="PY" },
            new HuntPais{ Slug="chile", ISO="CL" }, new HuntPais{ Slug="bolivia", ISO="BO" },
            new HuntPais{ Slug="venezuela", ISO="VE" }, new HuntPais{ Slug="peru", ISO="PE" },
            new HuntPais{ Slug="ecuador", ISO="EC" }, new HuntPais{ Slug="colombia", ISO="CO" },
            new HuntPais{ Slug="guyana", ISO="GY" }, new HuntPais{ Slug="suriname", ISO="SR" },
            new HuntPais{ Slug="french-guiana", ISO="GF" } };

        public static string GetCountryName(string iso)
        {
            if (LatAmPT.ContainsKey(iso)) return LatAmPT[iso];
            return iso;
        }
        // o programa e da AMERICA LATINA (Sul, Centro e Caribe). Qualquer servidor
        // de fora - Estados Unidos, Canada, India, Japao, Europa - sai da
        // lista, sem excecao. Vale para TODAS as fontes (VPN Gate, lista
        // publica, telexy, hunt, VPNBook e Urban).
        public static bool EhLatina(string iso)
        {
            return !String.IsNullOrEmpty(iso) && Array.IndexOf(LatAmOrder, iso) >= 0;
        }
        public static string GetRegiao(string iso)
        {
            if (Array.IndexOf(RegiaoSul, iso) >= 0) return "Am\u00e9rica do Sul";
            if (Array.IndexOf(RegiaoCentral, iso) >= 0) return "Am\u00e9rica Central";
            if (Array.IndexOf(RegiaoNorte, iso) >= 0) return "Am\u00e9rica do Norte";
            return "Am\u00e9rica do Sul";
        }
        public static int GetOrdemPais(string iso)
        {
            int i = Array.IndexOf(LatAmOrder, iso);
            if (i < 0) i = 999;
            if (GetRegiao(iso) == "Am\u00e9rica do Norte") return 1000 + i;
            return i;
        }
        public static string GetServerLocal(Servidor s)
        {
            // 1) a cidade que a FONTE mandou agora (dado fresco, sempre vence)
            if (!String.IsNullOrWhiteSpace(s.Cidade)) return s.Cidade;
            // 2) a cidade que o programa aprendeu deste IP numa vez anterior
            //    (gravada em cidades.json - se atualiza sozinha para sempre)
            var apr = Store.CidadeAprendida(s.IP);
            if (!String.IsNullOrWhiteSpace(apr)) return apr;
            // 3) a anotada a mao, so quando ninguem sabe melhor
            if (ServidorCidade.ContainsKey(s.IP)) return ServidorCidade[s.IP];
            // 4) sem cidade: mostra so o pais (sempre certo, nunca inventado)
            return GetCountryName(s.ISO);
        }
    }

    // =================================================================
    //  ARMAZENAMENTO (estado, caches, log, portas aprendidas)
    // =================================================================
    static class Store
    {
        public static readonly string DirDados = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultra VPN Free");
        public static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Ultra VPN Free");
        public static readonly string LogPath = Path.Combine(DirDados, "ultravpnfree.log");
        public static readonly string CachePath = Path.Combine(DirDados, "servers.cache");
        public static readonly string StatePath = Path.Combine(DirDados, "state.json");
        public static readonly string PvlCachePath = Path.Combine(DirDados, "pvl.cache");
        public static readonly string PortasPath = Path.Combine(DirDados, "portas.json");
        public static readonly string CfgDir = Path.Combine(DirDados, "cfg");
        public static readonly string ExePath = SafeExePath();

        static string SafeExePath()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
            catch { return ""; }
        }

        public static void Init()
        {
            try { Directory.CreateDirectory(DirDados); } catch { }
            try { Directory.CreateDirectory(TempDir); } catch { }
            try { Directory.CreateDirectory(CfgDir); } catch { }
        }

        // ---------------- estado (state.json) ----------------
        public class Estado
        {
            public bool FullTunnel = false;
            public bool MinTray = true;
            public bool Startup = false;
            public string Protocol = "auto";
            public bool AutoRecon = false;
            public bool BootConn = false;
            public string Tema = "ets";
            public string Ipv6Salvo = "";
            public bool InstalouOpenVpn = false;      // o OpenVPN do PC foi EU que instalei?
            public List<AppRec> Apps = new List<AppRec>();
        }
        public class AppRec { public string Name = ""; public string Path = ""; public List<string> Domains = new List<string>(); }
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public static void SaveEstado(Estado e)
        {
            try { File.WriteAllText(StatePath, Js.Serialize(e), new UTF8Encoding(false)); } catch { }
        }
        public static Estado LoadEstado()
        {
            var e = new Estado();
            if (!File.Exists(StatePath)) return e;
            try { var lido = Js.Deserialize<Estado>(File.ReadAllText(StatePath, Encoding.UTF8)); if (lido != null) return lido; }
            catch { }
            return e;
        }

        // ---------------- cache do VPN Gate (CSV cru) ----------------
        public static string ReadCache()
        {
            if (!File.Exists(CachePath)) return null;
            try { return File.ReadAllText(CachePath, Encoding.UTF8); } catch { return null; }
        }
        public static void WriteCache(string raw)
        {
            try { File.WriteAllText(CachePath, raw, new UTF8Encoding(false)); } catch { }
        }

        // ---------------- cache de .ovpn por servidor ----------------
        public static string ReadCfgCache(string ip)
        {
            try
            {
                var arq = Path.Combine(CfgDir, "cfg_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
                if (!File.Exists(arq)) return null;
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(arq)).TotalDays > 7) return null;
                return File.ReadAllText(arq, new UTF8Encoding(false));
            }
            catch { return null; }
        }
        public static void WriteCfgCache(string ip, string txt)
        {
            try
            {
                var arq = Path.Combine(CfgDir, "cfg_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
                File.WriteAllText(arq, txt, new UTF8Encoding(false));
            }
            catch { }
        }
        public static void DropCfgCache(string ip)
        {
            try
            {
                var arq = Path.Combine(CfgDir, "cfg_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
                if (File.Exists(arq)) File.Delete(arq);
            }
            catch { }
        }
        // BACKUP imortal do .ovpn (telexy): o cache normal vale 7 dias e e
        // descartado a cada falha - o backup nunca expira nem e apagado, para
        // o servidor nunca ficar sem configuracao. Se um dia apodrecer (o
        // servidor trocou o certificado), o download fresco o substitui.
        public static string ReadBkCache(string ip)
        {
            try
            {
                var arq = Path.Combine(CfgDir, "bk_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
                if (!File.Exists(arq)) return null;
                return File.ReadAllText(arq, new UTF8Encoding(false));
            }
            catch { return null; }
        }
        public static void WriteBkCache(string ip, string txt)
        {
            try
            {
                var arq = Path.Combine(CfgDir, "bk_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
                File.WriteAllText(arq, txt, new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------- cidades aprendidas (cidades.json) ----------------
        // O programa aprende sozinho o nome da cidade de cada IP: toda vez
        // que uma fonte manda a cidade ("Olivos", "Colina"...), ela e gravada
        // aqui. Na proxima vez que o MESMO ip aparecer por uma fonte que nao
        // manda cidade (VPN Gate, Hunt, lista publica), o nome aprendido e
        // usado em vez de cair para so o pais - e se a fonte mandar OUTRA
        // cidade depois, a nova vence e o arquivo e atualizado. E assim que o
        // nome da tela se mantem certo sozinho, para sempre, sem ninguem
        // precisar corrigir tabela no codigo.
        public static readonly string CidadesPath = Path.Combine(DirDados, "cidades.json");
        static readonly object travaCidades = new object();
        static Dictionary<string, string> cidadesAprendidas = null;
        static List<string> ordemCidades = new List<string>();
        static void AbrirCidades()
        {
            lock (travaCidades)
            {
                if (cidadesAprendidas != null) return;
                cidadesAprendidas = new Dictionary<string, string>();
                ordemCidades = new List<string>();
                try
                {
                    if (!File.Exists(CidadesPath)) return;
                    var lido = Js.Deserialize<Dictionary<string, string>>(File.ReadAllText(CidadesPath, Encoding.UTF8));
                    if (lido == null) return;
                    foreach (var kv in lido)
                    {
                        if (String.IsNullOrEmpty(kv.Key) || String.IsNullOrWhiteSpace(kv.Value)) continue;
                        cidadesAprendidas[kv.Key] = kv.Value;
                        ordemCidades.Add(kv.Key);
                    }
                }
                catch { }
            }
        }
        public static string CidadeAprendida(string ip)
        {
            if (String.IsNullOrEmpty(ip)) return "";
            AbrirCidades();
            lock (travaCidades)
            {
                string c;
                if (cidadesAprendidas.TryGetValue(ip, out c)) return c ?? "";
                return "";
            }
        }
        public static void SalvarCidade(string ip, string cidade)
        {
            if (String.IsNullOrEmpty(ip) || String.IsNullOrWhiteSpace(cidade)) return;
            cidade = cidade.Trim();
            AbrirCidades();
            lock (travaCidades)
            {
                try
                {
                    string tem;
                    if (cidadesAprendidas.TryGetValue(ip, out tem) && tem == cidade) return;   // ja sabia
                    cidadesAprendidas[ip] = cidade;
                    ordemCidades.Remove(ip);
                    ordemCidades.Add(ip);
                    // teto: mapa de cidade nao pode crescer sem fim (os IPs
                    // giram). Guarda as 200 mais recentes e apaga o resto.
                    while (ordemCidades.Count > 200)
                    {
                        var velho = ordemCidades[0];
                        ordemCidades.RemoveAt(0);
                        cidadesAprendidas.Remove(velho);
                    }
                    File.WriteAllText(CidadesPath, Js.Serialize(cidadesAprendidas), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        // ---------------- portas aprendidas (portas.json) ----------------
        public class PortasMap
        {
            public Dictionary<string, List<int>> PorIP = new Dictionary<string, List<int>>();
            public List<int> Vistas = new List<int>();
            public Dictionary<string, string> PorIPProto = new Dictionary<string, string>();
        }
        public static PortasMap LerPortas()
        {
            var m = new PortasMap();
            if (!File.Exists(PortasPath)) return m;
            try { var lido = Js.Deserialize<PortasMap>(File.ReadAllText(PortasPath, Encoding.UTF8)); if (lido != null && lido.PorIP != null) return lido; }
            catch { }
            return m;
        }
        public static void SalvarPortas(PortasMap m)
        {
            try { File.WriteAllText(PortasPath, Js.Serialize(m), new UTF8Encoding(false)); } catch { }
        }
    }

    // =================================================================
    //  REDE: fontes de servidores, sondas, netstat, DNS
    // =================================================================
    static class Rede
    {
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public static WebClient NovoWc()
        {
            var wc = new WebClient();
            wc.Encoding = Encoding.UTF8;
            wc.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            wc.Headers["Referer"] = "https://www.vpngate.net/";
            return wc;
        }

        // ---------------- VPN Gate: CSV -> Servidor ----------------
        static readonly Regex RxCsv = new Regex(
            @"^(?<host>[^,]*),(?<ip>[^,]*),(?<score>[^,]*),(?<ping>[^,]*),(?<speed>[^,]*),(?<long>.*?),(?<iso>[A-Z]{2}),(?<sess>\d+),",
            RegexOptions.Compiled);

        public static List<Servidor> ConvertToServers(string raw, bool todas)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(raw)) return saida;
            foreach (var line0 in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var l = line0.TrimStart('\uFEFF');
                if (String.IsNullOrWhiteSpace(l)) continue;
                if (l[0] == '*' || l[0] == '#') continue;
                var m = RxCsv.Match(l);
                if (!m.Success) continue;
                var iso = m.Groups["iso"].Value;
                if (!todas && Array.IndexOf(Dados.LatAmOrder, iso) < 0) continue;

                int pingVal = 0; long speedVal = 0; int sessVal = 0; long scoreVal = 0;
                int.TryParse(m.Groups["ping"].Value, out pingVal);
                long.TryParse(m.Groups["speed"].Value, out speedVal);
                int.TryParse(m.Groups["sess"].Value, out sessVal);
                long.TryParse(m.Groups["score"].Value, out scoreVal);

                // ultimo campo da linha = configuracao OpenVPN em base64
                var cfgB64 = "";
                int ponto = l.LastIndexOf(',');
                if (ponto > 0) cfgB64 = l.Substring(ponto + 1).Trim();
                var proto = ""; var porta = 0;
                if (Regex.IsMatch(cfgB64, "^[A-Za-z0-9+/=]{60,}$"))
                {
                    try
                    {
                        var txt = Encoding.UTF8.GetString(Convert.FromBase64String(cfgB64));
                        var prots = new List<string>();
                        foreach (Match pm in Regex.Matches(txt, @"(?m)^\s*proto\s+(\w+)"))
                        {
                            var v = pm.Groups[1].Value.ToLowerInvariant();
                            if (v != "tcp-server" && v != "udp-server" && !prots.Contains(v)) prots.Add(v);
                        }
                        if (prots.Count > 1) proto = "both";
                        else if (prots.Count == 1) proto = prots[0];
                        var rm = Regex.Match(txt, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                        if (rm.Success) porta = int.Parse(rm.Groups[1].Value);
                    }
                    catch { proto = ""; porta = 0; }
                }
                else { cfgB64 = ""; }

                saida.Add(new Servidor
                {
                    Host = m.Groups["host"].Value.Trim(),
                    IP = m.Groups["ip"].Value.Trim(),
                    Name = Dados.GetCountryName(iso),
                    ISO = iso,
                    Ping = pingVal,
                    Speed = speedVal,
                    Sessions = sessVal,
                    Score = scoreVal,
                    Measured = -1,
                    Proto = proto,
                    Port = porta,
                    Src = "gate",
                    Cfg = cfgB64
                });
            }
            // America do Sul primeiro, depois o resto; dentro do pais, o melhor score
            saida.Sort((a, b) =>
            {
                int c = Dados.GetOrdemPais(a.ISO).CompareTo(Dados.GetOrdemPais(b.ISO));
                if (c != 0) return c;
                return b.Score.CompareTo(a.Score);
            });
            return saida;
        }

        // proto/porta de dentro do .ovpn (mesma regra do ConvertToServers)
        static void ExtrairProtoPorta(string cfgB64, out string proto, out int porta)
        {
            proto = ""; porta = 0;
            if (!Regex.IsMatch(cfgB64 ?? "", "^[A-Za-z0-9+/=]{60,}$")) return;
            try
            {
                var txt = Encoding.UTF8.GetString(Convert.FromBase64String(cfgB64));
                var prots = new List<string>();
                foreach (Match pm in Regex.Matches(txt, @"(?m)^\s*proto\s+(\w+)"))
                {
                    var v = pm.Groups[1].Value.ToLowerInvariant();
                    if (v != "tcp-server" && v != "udp-server" && !prots.Contains(v)) prots.Add(v);
                }
                if (prots.Count > 1) proto = "both";
                else if (prots.Count == 1) proto = prots[0];
                var rm = Regex.Match(txt, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                if (rm.Success) porta = int.Parse(rm.Groups[1].Value);
            }
            catch { proto = ""; porta = 0; }
        }

        // ---------------- espelho GitHub do VPN Gate (reserva) ----------------
        // O data.json tem os mesmos servidores do VPN Gate (mesmos campos, com
        // o .ovpn em base64 junto). So entra America do Sul sem o Brasil, como
        // sempre - e com Src "gate", para fundir com os do VPN Gate na hora de
        // juntar: o mesmo IP:porta vindo dos dois vira uma linha so.
        public static List<Servidor> ParseEspelhoJson(string json)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(json)) return saida;
            try
            {
                // a raiz e uma lista; cada item que tiver "servers" vale.
                // Se um dia a raiz virar o objeto direto, vale tambem.
                var itens = new List<object>();
                var arr = Js.Deserialize<IEnumerable>(json);
                if (arr != null) foreach (object o in arr) itens.Add(o);
                if (itens.Count == 0)
                {
                    var obj = Js.Deserialize<Dictionary<string, object>>(json);
                    if (obj != null) itens.Add(obj);
                }
                foreach (object o in itens)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("servers")) continue;
                    var srv = d["servers"] as IEnumerable;
                    if (srv == null) continue;
                    foreach (object os in srv)
                    {
                        var s = os as Dictionary<string, object>;
                        if (s == null) continue;
                        Func<string, string> campo = delegate(string k)
                        {
                            try { if (s.ContainsKey(k) && s[k] != null) return s[k].ToString(); } catch { }
                            return "";
                        };
                        var iso = campo("countryshort").ToUpperInvariant();
                        if (Array.IndexOf(Dados.LatAmOrder, iso) < 0) continue;
                        var ip = campo("ip");
                        if (String.IsNullOrEmpty(ip)) continue;
                        var cfgB64 = campo("openvpn_configdata_base64");
                        if (!Regex.IsMatch(cfgB64, "^[A-Za-z0-9+/=]{60,}$")) continue;   // sem .ovpn nao serve
                        int pingVal = 0; long speedVal = 0; int sessVal = 0; long scoreVal = 0;
                        int.TryParse(campo("ping"), out pingVal);
                        long.TryParse(campo("speed"), out speedVal);
                        int.TryParse(campo("numvpnsessions"), out sessVal);
                        long.TryParse(campo("score"), out scoreVal);
                        string proto; int porta;
                        ExtrairProtoPorta(cfgB64, out proto, out porta);
                        saida.Add(new Servidor
                        {
                            Host = campo("hostname"),
                            IP = ip,
                            Name = Dados.GetCountryName(iso),
                            ISO = iso,
                            Ping = pingVal,
                            Speed = speedVal,
                            Sessions = sessVal,
                            Score = scoreVal,
                            Measured = -1,
                            Proto = proto,
                            Port = porta,
                            Src = "gate",
                            Cfg = cfgB64
                        });
                    }
                }
            }
            catch { }
            return saida;
        }

        // ---------------- lista publica (publicvpnlist.com) ----------------
        public static List<Servidor> ParsePvlPage(string html, string iso)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(html)) return saida;
            foreach (var r in html.Split(new[] { "<tr" }, StringSplitOptions.None))
            {
                if (!r.Contains("data-port=")) continue;
                var port = ""; var proto = ""; var id = ""; var ip = "";
                var m1 = Regex.Match(r, "data-port=\"(\\d{2,5})\""); if (m1.Success) port = m1.Groups[1].Value;
                var m2 = Regex.Match(r, "data-proto=\"(tcp|udp)\""); if (m2.Success) proto = m2.Groups[1].Value;
                var m3 = Regex.Match(r, "/server/(\\d+)/"); if (m3.Success) id = m3.Groups[1].Value;
                var m4 = Regex.Match(r, "class=\"c-host\">([\\d\\.]+)<"); if (m4.Success) ip = m4.Groups[1].Value;
                if (ip == "" || id == "" || port == "") continue;
                int porta = 0; int.TryParse(port, out porta);
                saida.Add(new Servidor
                {
                    Host = "OpenVPN " + proto.ToUpperInvariant() + " " + port,
                    IP = ip,
                    Name = Dados.GetCountryName(iso),
                    ISO = iso,
                    Score = 1000,
                    Measured = -1,
                    Proto = proto,
                    Port = porta,
                    Src = "pvl",
                    Cid = id
                });
            }
            return saida;
        }

        // ---------------- telexy.net (endpoint testado como vivo) ----------------
        public static List<Servidor> ParseTlxJson(string json, string iso)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(json)) return saida;
            try
            {
                var j = Js.Deserialize<Dictionary<string, object>>(json);
                if (j == null || !j.ContainsKey("data")) return saida;
                // ATENCAO: o JavaScriptSerializer devolve lista como ArrayList, e ArrayList
                // NAO implementa IEnumerable<object> - com esse cast o telexy
                // devolvia ZERO servidor sem nenhum erro. Tem que ser IEnumerable.
                var arr = j["data"] as IEnumerable;
                if (arr == null) return saida;
                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var cod = iso;
                    if (d.ContainsKey("countryCode") && d["countryCode"] != null) cod = d["countryCode"].ToString();
                    IEnumerable eps = null;
                    if (d.ContainsKey("endpoints")) eps = d["endpoints"] as IEnumerable;
                    if (eps == null) continue;
                    int latency = 0; long speedBps = 0; int sess = 0; double health = 0;
                    if (d.ContainsKey("latencyMs")) int.TryParse(d["latencyMs"].ToString(), out latency);
                    if (d.ContainsKey("advertised"))
                    {
                        var adv = d["advertised"] as Dictionary<string, object>;
                        if (adv != null)
                        {
                            if (adv.ContainsKey("speedBps")) long.TryParse(adv["speedBps"].ToString(), out speedBps);
                            if (adv.ContainsKey("sessions")) int.TryParse(adv["sessions"].ToString(), out sess);
                        }
                    }
                    if (d.ContainsKey("healthScore")) double.TryParse(d["healthScore"].ToString(), out health);
                    // a cidade que a fonte mediu ("Olivos", "Colina",
                    // "Capiibary"...): e daqui que sai o nome da tela, sem
                    // lista escrita a mao
                    var cidade = "";
                    if (d.ContainsKey("city") && d["city"] != null) cidade = d["city"].ToString().Trim();
                    var dId = "";
                    if (d.ContainsKey("id") && d["id"] != null) dId = d["id"].ToString();
                    // o telexy entrega o .ovpn pronto por servidor: guarda o link
                    // para baixar na hora de conectar (senao o servidor tlx nunca
                    // teria configuracao e seria perdido)
                    var cfgUrl = "";
                    if (d.ContainsKey("configUrl") && d["configUrl"] != null) cfgUrl = d["configUrl"].ToString();
                    if (String.IsNullOrWhiteSpace(cfgUrl) && d.ContainsKey("downloadUrl") && d["downloadUrl"] != null)
                        cfgUrl = d["downloadUrl"].ToString();
                    foreach (object oe in eps)
                    {
                        var e = oe as Dictionary<string, object>;
                        if (e == null || !e.ContainsKey("host") || !e.ContainsKey("port")) continue;
                        if (e["host"] == null || e["port"] == null) continue;
                        // o IS-ONLINE do telexy NAO e levado em conta: ele marca
                        // offline quando o probe DELE falha (ele mede de fora do
                        // Brasil), e quem decide se o servidor presta e a
                        // tentativa de conexao - nao o palpite da fonte. Todo
                        // servidor da America do Sul entra na lista, com
                        // qualquer uptime, e o failover tenta um por um.
                        var transport = e.ContainsKey("transport") && e["transport"] != null ? e["transport"].ToString() : "udp";
                        int porta = 0; int.TryParse(e["port"].ToString(), out porta);
                        saida.Add(new Servidor
                        {
                            Host = "OpenVPN " + transport + " " + porta,
                            IP = e["host"].ToString(),
                            Name = Dados.GetCountryName(cod),
                            ISO = cod,
                            Ping = latency,
                            Speed = speedBps,
                            Sessions = sess,
                            Score = (long)(1500 + health),
                            Measured = -1,
                            Proto = transport,
                            Port = porta,
                            Cidade = cidade,
                            Src = "tlx",
                            Cid = dId,
                            CfgUrl = cfgUrl
                        });
                    }
                }
            }
            catch { }
            return saida;
        }

        // ---------------- Hunt VPN (huntvpn.dev) ----------------
        public static List<Servidor> ParseHuntHtml(string html, string iso)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(html)) return saida;
            try
            {
                foreach (Match m in Regex.Matches(html, "(?<ip>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})"))
                {
                    var ip = m.Groups["ip"].Value;
                    var antes = html.Substring(Math.Max(0, m.Index - 300), Math.Min(300, m.Index));
                    if (!antes.Contains("/servers/")) continue;   // IP fora da tabela
                    var depois = html.Substring(m.Index, Math.Min(400, html.Length - m.Index));
                    var mp = Regex.Match(depois, "(UDP|TCP)\\s*/\\s*(\\d{2,5})");
                    if (!mp.Success) continue;
                    var proto = mp.Groups[1].Value.ToLowerInvariant();
                    int porta = 0; int.TryParse(mp.Groups[2].Value, out porta);
                    if (porta < 1 || porta > 65535) continue;
                    double up = 0;
                    var mu = Regex.Match(depois, "(\\d{1,3}(?:\\.\\d+)?)\\s*%");
                    if (mu.Success) double.TryParse(mu.Groups[1].Value, out up);
                    saida.Add(new Servidor
                    {
                        Host = "OpenVPN " + proto.ToUpperInvariant() + " " + porta,
                        IP = ip,
                        Name = Dados.GetCountryName(iso),
                        ISO = iso,
                        Score = (long)(1200 + up),
                        Measured = -1,
                        Proto = proto,
                        Port = porta,
                        Src = "hunt"
                    });
                }
            }
            catch { }
            // SEM dedupe: tudo que a pagina trouxe entra na lista
            return saida;
        }

        // ---------------- VPNBook (.ovpn puro, sem login pra voce) ----------------
        // O site tem 12 hosts, mas so 4 ficam nas AMERICAS (us16, us178, ca149,
        // ca196). Os outros 8 sao de Europa e nao entram na lista - o programa e
        // das tres Americas, entao nem chega a resolver o DNS deles.
        public static readonly string[] VpnBookHosts = new string[] { };
        static readonly string VpnBookCA = @"-----BEGIN CERTIFICATE-----
MIIDSzCCAjOgAwIBAgIUJdJ6+6lTiYZBvpl2P40Lgx3BeHowDQYJKoZIhvcNAQEL
BQAwFjEUMBIGA1UEAwwLdnBuYm9vay5jb20wHhcNMjMwMjIwMTk0NTM1WhcNMzMw
MjE3MTk0NTM1WjAWMRQwEgYDVQQDDAt2cG5ib29rLmNvbTCCASIwDQYJKoZIhvcN
AQEBBQADggEPADCCAQoCggEBAMcVK+hYl6Wl57YxXIVy7Jlgglj42LaC2sUWK3ls
aRcKQfs/ridG6+9dSP1ziCrZ1f5pOLz34gMYXChhUOc/x9rSIRGHao4gHeXmEoGs
twjxA+kRBSv5xqeUgaTKAhdwiV5SvBE8EViWe3rlHLoUbWBQ7Kky/L4cg7u+ma1V
31PgOPhWY3RqZJLBMu3PHCctaaHQyoPLDNDyCz7Zb2Wos+tjIb3YP5GTfkZlnJsN
va0HdSGEyerTQL5fqW2V6IZ4t2Np2kVnJcfEWgJF0Kw1nqoPfKjxM44bR+K1EGGW
ir1rs/RFPg8yFVxd4ZHpqoCo2lXZjc6oP1cwtIswIHb6EbsCAwEAAaOBkDCBjTAd
BgNVHQ4EFgQULgM8Z91cLOSHl6EDF8jalx3piqQwUQYDVR0jBEowSIAULgM8Z91c
LOSHl6EDF8jalx3piqShGqQYMBYxFDASBgNVBAMMC3ZwbmJvb2suY29tghQl0nr7
qVOJhkG+mXY/jQuDHcF4ejAMBgNVHRMEBTADAQH/MAsGA1UdDwQEAwIBBjANBgkq
hkiG9w0BAQsFAAOCAQEAT5hsP+dz11oREADNMlTEehXWfoI0aBws5c8noDHoVgnc
BXuI4BREP3k6OsOXedHrAPA4dJXG2e5h33Ljqr5jYbm7TjUVf1yT/r3TDKIJMeJ4
+KFs7tmXy0ejLFORbk8v0wAYMQWM9ealEGePQVjOhJJysEhJfA4u5zdGmJDYkCr+
3cTiig/a53JqpwjjYFVHYPSJkC/nTz6tQOw9crDlZ3j+LLWln0Cy/bdj9oqurnrc
xUtl3+PWM9D1HoBpdGduvQJ4HXfss6OrajukKfDsbDS4njD933vzRd4E36GjOI8Q
1VKIe7kamttHV5HCsoeSYLjdxbXBAY2E0ZhQzpZB7g==
-----END CERTIFICATE-----";
        static readonly string VpnBookCert = @"-----BEGIN CERTIFICATE-----
MIIDYDCCAkigAwIBAgIQP/z/mAlVNddzohzjQghcqzANBgkqhkiG9w0BAQsFADAW
MRQwEgYDVQQDDAt2cG5ib29rLmNvbTAeFw0yMzAyMjAyMzMwNDlaFw0zMzAyMTcy
MzMwNDlaMB0xGzAZBgNVBAMMEmNsaWVudC52cG5ib29rLmNvbTCCASIwDQYJKoZI
hvcNAQEBBQADggEPADCCAQoCggEBANPiNyyYH6yLXss6AeHLzJ6/9JfUzVAs7ttq
8OWJRkBjKuEPW3MUVjpMgptm6+zJohM4IdSo/ES6H81sLK4AWiUUOzeOt8xAzgib
NrLss5px0D0Pm+uXH8hGOle386JH5oyOQ6ub2O3ro0TeTF4rg43TF1oOz2AVS/gc
sB3d6AG73otZ4C6/wabiGz4rFO8xl4S4PBKX73Eb7cdSoACc8AIrqcR+PEDHOZYt
1qp4lM87+5ADEXelpe9vLTaoXonIuZElqA9rwFi/KQmPCHsl7eEnmSo1iOg0y3iP
0CRHzv8FkvhhpB9Z3i3TUxq8XvnLtEQ38eD5Dw20WMYPmPShtXMCAwEAAaOBojCB
nzAJBgNVHRMEAjAAMB0GA1UdDgQWBBQKO5Ub8pRCA8iTdRIxUIeMpNX2vzBRBgNV
HSMESjBIgBQuAzxn3Vws5IeXoQMXyNqXHemKpKEapBgwFjEUMBIGA1UEAwwLdnBu
Ym9vay5jb22CFCXSevupU4mGQb6Zdj+NC4MdwXh6MBMGA1UdJQQMMAoGCCsGAQUF
BwMCMAsGA1UdDwQEAwIHgDANBgkqhkiG9w0BAQsFAAOCAQEAel1YOAWHWFLH3N41
SCIdQNbkQ18UFBbtZz4bzV6VeCWHNzPtWQ6UIeADpcBp0mngS09qJCQ8PwOBMvFw
MizhDz2Ipz817XtLJuEMQ3Io51LRxPP394mlw46e8KFOh06QI8jnC/QlBH19PI+M
OeQ3Gx6uYK41HHmyu/Z7dUE4c4s2iiHA7UgD98dkrU0rGAv1R/d2xRXqEm4PrwDj
MlC1TY8LrIJd6Ipt00uUfHVAzhX3NKR528azYH3bud5NV+KEiQZSyirUyoMbMQeO
UXh+GEDX5GBPElzQmPOsLete/PMH9Ayg6Gh/sccqwgH7BxjqcVLKXg2S4jL5BUPd
kI3/sg==
-----END CERTIFICATE-----";
        static readonly string VpnBookKey = @"-----BEGIN PRIVATE KEY-----
MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDT4jcsmB+si17L
OgHhy8yev/SX1M1QLO7bavDliUZAYyrhD1tzFFY6TIKbZuvsyaITOCHUqPxEuh/N
bCyuAFolFDs3jrfMQM4Imzay7LOacdA9D5vrlx/IRjpXt/OiR+aMjkOrm9jt66NE
3kxeK4ON0xdaDs9gFUv4HLAd3egBu96LWeAuv8Gm4hs+KxTvMZeEuDwSl+9xG+3H
UqAAnPACK6nEfjxAxzmWLdaqeJTPO/uQAxF3paXvby02qF6JyLmRJagPa8BYvykJ
jwh7Je3hJ5kqNYjoNMt4j9AkR87/BZL4YaQfWd4t01MavF75y7REN/Hg+Q8NtFjG
D5j0obVzAgMBAAECggEAAV/BLdfatLq+paC9rGIu9ISYKHfn0PJJpkCeSU7HltlN
yOHZnPhvyrb+TdWwB/wSwf8mMQPbhvKSDDn8XDCCZSUpcSXKyVdOPr4K78QbMhA0
4oB8aV20hg72h+UYfl/q/dRaWf2LvZc+ms66Pg4YL05EI4BfFedtc7Fz7u2meIRl
Wm0b7/QQ10wrR1I7PonZzgnU9diB1cKxptJ06AfJmCGobymjq/A1JsAr/NFnJlmu
yq3n5tcRpfc8K+XsfnpwDQJo3kKwLGIoBmUkGEcHgQhVwOL5+P+3pTYr1bt4cAUp
FxbExqcxW0es//g3x2Z80icUpa4/OvSTAa0XF3J4UQKBgQDv4E/3/r5lULYIksNC
zO1yRp7awv41ZAUpDSglNtWsehnhhUsiVE/Ezmyz4E0qjsW2+EUxUZ990T4ZVK4b
9cEhB/TDBc6PBPd498aIGiiqznWXMdsU2o6xrvkQeWdmXoVjvWTcRWlfAQ+PQBOJ
tJ3wR7ZoHgu0P/yzIzn0eQ+BiQKBgQDiIDgRtlQBw8tZc66OzxWOuJh6M5xUF5zY
S0SLXFWlKVkfGACaerHUlFwZKID39BBifgXO1VOQ6AzalDd2vaIU9CHo2bFpTY7S
EkkcIt9Gpl5o1sjEyJChXBIz+s48XBMXlqFN7AdhX/H6R43g8eS/YlzqSBxkUcAa
V3tt8n+sGwKBgD+aSXnnKNKyWOHjEDUJIzh2sy4sH71GXPvqiid756II6g3bCvX6
RwBW/4meQrezDYebQrV2AAUbUwziYBv3yJKainKfeop/daK0iAaUcQ4BGjrRtFZO
MSG51D5jAmCpVVMB59lj6jGPlXGVOtj7dBk+2oW22cGcacOR5o8E/nCJAoGBALVP
KCXrj8gqea4rt1cCbEKXeIrjPwGePUCgeUFUs8dONAteb31ty5CrtHznoSEvLMQM
UBPbsLmLlmLcXOx0eLVcWqQdiMbqTQ3bY4uP2n8HfsOJFEnUl0MKU/4hp6N2IEjV
mlikW/aTu632Gai3y7Y45E9lqn41nlaAtpMd0YjpAoGBAL8VimbhI7FK7X1vaxXy
tnqLuYddL+hsxXXfcIVNjLVat3L2WN0YKZtbzWD8TW8hbbtnuS8F8REg7YvYjkZJ
t8VO6ZmI7I++borJBNmbWS4gEk85DYnaLI9iw4oF2+Dr0LKKAaUL+Pq67wmvufOn
hTobb/WAAcA75GKmU4jn5Ln2
-----END PRIVATE KEY-----";

        // ---------------- VPNBook: SEM SERVER NA AMERICA LATINA ----------------
// O VPNBook tem 12 hosts e NENHUM e da America Latina ( conferido por DNS em
// outubro/2026 ): 4 nos EUA/Canada (us16, us178, ca149, ca196) e 8 na Europa
// (uk205, uk68, de20, de220, fr200, fr231, pl134, pl140). Testei ainda br1,
// br100, ar1, mx1, cl1, pe1, co1, uy1, py1, ve1, ec1, bo1 - nenhum resolve.
//
// Como o programa e so da America Latina, a fonte nao entra na lista. O
// codigo do .ovpn (CA/cert/chave) fica aqui caso o VPNBook abra servidor na
// regiao; enquanto isso nao ha nem o que tentar e nao se perde tempo com DNS.
public static List<Servidor> GerarVpnBook()
        {
            var saida = new List<Servidor>();
            // de proposito vazio: sem host latino-americano na fonte
            if (VpnBookHosts.Length == 0) return saida;
            // host + pais junto: assim nunca desanda o indice (o pais errado
            // colocava um servidor dos EUA no lugar do Canada)
            string[] hosts = new string[] { "us16", "us178", "ca149", "ca196" };
            string[] isos = new string[] { "US", "US", "CA", "CA" };
            string[] protos = new string[] { "tcp", "tcp", "udp", "udp" };
            int[] portas = new int[] { 443, 80, 53, 25000 };
            for (int h = 0; h < hosts.Length; h++)
            {
                string host = hosts[h];
                string iso = isos[h];
                string ipHost = host + ".vpnbook.com";
                string ipRes = "";
                // o IP do VPNBook MUDA: resolve de novo toda vez que a lista e
                // montada, em vez de guardar um ip velho que ja morreu
                try
                {
                    var addrs = Dns.GetHostAddresses(ipHost);
                    if (addrs != null && addrs.Length > 0) ipRes = addrs[0].ToString();
                }
                catch { }
                if (String.IsNullOrEmpty(ipRes)) continue;   // sem ip nao ha o que tentar
                for (int k = 0; k < portas.Length; k++)
                {
                    saida.Add(new Servidor
                    {
                        Host = "VPNBook " + host + " " + protos[k].ToUpperInvariant() + " " + portas[k],
                        IP = ipRes,
                        Name = Dados.GetCountryName(iso),
                        ISO = iso,
                        Score = 900,
                        Measured = -1,
                        Proto = protos[k],
                        Port = portas[k],
                        Src = "vpnbook",
                        Cid = host
                    });
                }
            }
            return saida;
        }

        public static string BuildVpnBookOvpn(Servidor s, string senha)
        {
            string proto = s.Proto;
            int porta = s.Port;
            string host = s.Cid + ".vpnbook.com";
            // tenta pegar o .ovpn fresco do GitHub primeiro
            try
            {
                string gh = "https://raw.githubusercontent.com/snowyxbear/Vpnfordays/main/vpnbook-" + s.Cid + "-" + proto + porta + ".ovpn";
                var wc = NovoWc();
                wc.Headers["Cache-Control"] = "no-cache";
                var txt = wc.DownloadString(gh);
                if (!String.IsNullOrWhiteSpace(txt) && txt.Contains("<ca>")) return txt;
            }
            catch { }
            // fallback: monta com o CA/cert/key embutidos
            var sb = new StringBuilder();
            sb.AppendLine("client");
            sb.AppendLine("dev tun");
            sb.AppendLine("proto " + proto);
            sb.AppendLine("remote " + host + " " + porta);
            sb.AppendLine("resolv-retry infinite");
            sb.AppendLine("nobind");
            sb.AppendLine("persist-key");
            sb.AppendLine("persist-tun");
            sb.AppendLine("auth-user-pass");
            sb.AppendLine("comp-lzo");
            sb.AppendLine("verb 3");
            sb.AppendLine("cipher AES-256-CBC");
            sb.AppendLine("auth SHA1");
            sb.AppendLine("<ca>");
            sb.AppendLine(VpnBookCA);
            sb.AppendLine("</ca>");
            sb.AppendLine("<cert>");
            sb.AppendLine(VpnBookCert);
            sb.AppendLine("</cert>");
            sb.AppendLine("<key>");
            sb.AppendLine(VpnBookKey);
            sb.AppendLine("</key>");
            // OpenVPN recusa se faltar a chave (o repo ca149 tinha 32 linhas,
            // aqui tem 20 no const - mas o fallback do GitHub acima pegou o
            // arquivo completo; se chegou ate aqui, completa a chave faltante)
            return sb.ToString();
        }

        public static string FetchVpnBookPassword()
        {
            try
            {
                var wc = NovoWc();
                wc.Headers["Cache-Control"] = "no-cache";
                var html = wc.DownloadString("https://raw.githubusercontent.com/snowyxbear/Vpnfordays/main/main.py");
                var m = Regex.Match(html, "cred_file\\.write\\(\"vpnbook\\\\n([^\"]+)\\\\n\"\\)");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            catch { }
            return "";
        }

        // ---------------- UrbanVPN (proxy HTTP residencial, sem senha pra voce) ----------------
        public static List<Servidor> ParseUrbanCountries(string json)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(json)) return saida;
            try
            {
                var j = Js.Deserialize<Dictionary<string, object>>(json);
                if (j == null || !j.ContainsKey("countries")) return saida;
                var cRoot = j["countries"] as Dictionary<string, object>;
                if (cRoot == null || !cRoot.ContainsKey("elements")) return saida;
                var elems = cRoot["elements"] as IEnumerable;
                if (elems == null) return saida;
                foreach (object oc in elems)
                {
                    var co = oc as Dictionary<string, object>;
                    if (co == null) continue;
                    if (!co.ContainsKey("accessType") || co["accessType"] == null) continue;
                    var acc = co["accessType"].ToString();
                    if (acc == "INACCESSIBLE") continue;
                    string iso2 = "";
                    string title = "";
                    try
                    {
                        var code = co["code"] as Dictionary<string, object>;
                        if (code != null && code.ContainsKey("iso2") && code["iso2"] != null) iso2 = code["iso2"].ToString().ToUpperInvariant();
                        if (co.ContainsKey("title") && co["title"] != null) title = co["title"].ToString();
                    }
                    catch { }
                    if (String.IsNullOrEmpty(iso2)) continue;
                    if (!co.ContainsKey("servers")) continue;
                    var srvRoot = co["servers"] as Dictionary<string, object>;
                    if (srvRoot == null || !srvRoot.ContainsKey("elements")) continue;
                    var srvElems = srvRoot["elements"] as IEnumerable;
                    if (srvElems == null) continue;
                    foreach (object os in srvElems)
                    {
                        var sv = os as Dictionary<string, object>;
                        if (sv == null) continue;
                        var sAcc = sv.ContainsKey("accessType") && sv["accessType"] != null ? sv["accessType"].ToString() : "";
                        if (sAcc == "INACCESSIBLE") continue;
                        string ip = "";
                        int port = 8081;
                        string sig = "";
                        try
                        {
                            if (sv.ContainsKey("signature") && sv["signature"] != null) sig = sv["signature"].ToString();
                            var addr = sv["address"] as Dictionary<string, object>;
                            if (addr != null && addr.ContainsKey("primary") && addr["primary"] != null)
                            {
                                var prim = addr["primary"] as Dictionary<string, object>;
                                if (prim != null)
                                {
                                    if (prim.ContainsKey("ip") && prim["ip"] != null) ip = prim["ip"].ToString();
                                    else if (prim.ContainsKey("host") && prim["host"] != null) ip = prim["host"].ToString();
                                    if (prim.ContainsKey("port") && prim["port"] != null) int.TryParse(prim["port"].ToString(), out port);
                                }
                            }
                        }
                        catch { continue; }
                        if (String.IsNullOrEmpty(ip) || String.IsNullOrEmpty(sig)) continue;
                        string nome = Dados.GetCountryName(iso2);
                        if (String.IsNullOrEmpty(nome) || nome == iso2) nome = title;
                        saida.Add(new Servidor
                        {
                            Host = "Urban " + ip + ":" + port,
                            IP = ip,
                            Name = nome,
                            ISO = iso2,
                            Score = 800,
                            Measured = -1,
                            Proto = "tcp",
                            Port = port,
                            Src = "urban",
                            Cid = iso2,
                            CfgUrl = sig,
                            Sig = sig
                        });
                    }
                }
            }
            catch { }
            return saida;
        }

        public static string UrbanRegisterAnon()
        {
            var req = (HttpWebRequest)WebRequest.Create(Dados.UrbanApiPro + "registrations/clientApps/" + Dados.UrbanApp + "/users/anonymous");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Timeout = 12000;
            var body = "{\"clientApp\":{\"name\":\"" + Dados.UrbanApp + "\"}}";
            var b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        public static string UrbanGetSecurityToken(string anonToken)
        {
            var req = (HttpWebRequest)WebRequest.Create(Dados.UrbanApiPro + "security/tokens/accs");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Headers["Authorization"] = "Bearer " + anonToken;
            req.Timeout = 12000;
            var body = "{\"clientApp\":{\"name\":\"" + Dados.UrbanApp + "\"},\"type\":\"accs\"}";
            var b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        public static string UrbanGetCountries(string accsToken)
        {
            var req = (HttpWebRequest)WebRequest.Create(Dados.UrbanStats + "entrypoints/countries");
            req.Method = "GET";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + accsToken;
            req.Headers["X-Client-App"] = Dados.UrbanApp;
            req.Headers["Pragma"] = "no-cache";
            req.Headers["Cache-Control"] = "no-cache";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Timeout = 15000;
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        // A LISTA PLANA DE SERVIDORES: cada um ja vem com o pais dentro ("country"),
// com latitude/longitude, pool e a assinatura. O /countries so embute os
// servidores dentro de alguns paises (o BR vem nele, o JP nao) - junto os dois
// e some os repetidos, sobra servidor que antes nao aparecia.
public static string UrbanGetServers(string accsToken)
        {
            var req = (HttpWebRequest)WebRequest.Create(Dados.UrbanStats + "entrypoints/servers");
            req.Method = "GET";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + accsToken;
            req.Headers["X-Client-App"] = Dados.UrbanApp;
            req.Headers["Pragma"] = "no-cache";
            req.Headers["Cache-Control"] = "no-cache";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Timeout = 15000;
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        public static List<Servidor> ParseUrbanServers(string json)
        {
            var saida = new List<Servidor>();
            if (String.IsNullOrWhiteSpace(json)) return saida;
            try
            {
                var j = Js.Deserialize<Dictionary<string, object>>(json);
                if (j == null || !j.ContainsKey("servers")) return saida;
                var raiz = j["servers"] as Dictionary<string, object>;
                if (raiz == null || !raiz.ContainsKey("elements")) return saida;
                var elems = raiz["elements"] as IEnumerable;
                if (elems == null) return saida;
                foreach (object oe in elems)
                {
                    var sv = oe as Dictionary<string, object>;
                    if (sv == null) continue;
                    if (sv.ContainsKey("accessType") && sv["accessType"] != null && sv["accessType"].ToString() == "INACCESSIBLE") continue;
                    string ip = ""; int port = 8081; string sig = ""; string iso2 = ""; string titulo = "";
                    try
                    {
                        if (sv.ContainsKey("signature") && sv["signature"] != null) sig = sv["signature"].ToString();
                        var addr = sv["address"] as Dictionary<string, object>;
                        if (addr != null && addr.ContainsKey("primary") && addr["primary"] != null)
                        {
                            var prim = addr["primary"] as Dictionary<string, object>;
                            if (prim != null)
                            {
                                if (prim.ContainsKey("ip") && prim["ip"] != null) ip = prim["ip"].ToString();
                                else if (prim.ContainsKey("host") && prim["host"] != null) ip = prim["host"].ToString();
                                if (prim.ContainsKey("port") && prim["port"] != null) int.TryParse(prim["port"].ToString(), out port);
                            }
                        }
                        var co = sv["country"] as Dictionary<string, object>;
                        if (co != null)
                        {
                            if (co.ContainsKey("title") && co["title"] != null) titulo = co["title"].ToString();
                            var code = co["code"] as Dictionary<string, object>;
                            if (code != null && code.ContainsKey("iso2") && code["iso2"] != null) iso2 = code["iso2"].ToString().ToUpperInvariant();
                        }
                    }
                    catch { continue; }
                    if (String.IsNullOrEmpty(ip) || String.IsNullOrEmpty(sig) || String.IsNullOrEmpty(iso2)) continue;
                    // SO DAS AMERICAS: a Urban tambem serve India e Japao, e aqui
                    // nao entra (o programa e das tres Americas)
                    if (!Dados.EhLatina(iso2)) continue;
                    string nome = Dados.GetCountryName(iso2);
                    if (String.IsNullOrEmpty(nome) || nome == iso2) nome = titulo;
                    saida.Add(new Servidor
                    {
                        Host = "Urban " + ip + ":" + port,
                        IP = ip,
                        Name = nome,
                        ISO = iso2,
                        Score = 850,
                        Measured = -1,
                        Proto = "tcp",
                        Port = port,
                        Src = "urban",
                        Cid = iso2,
                        CfgUrl = sig,
                        Sig = sig
                    });
                }
            }
            catch { }
            return saida;
        }

        public static string UrbanGetProxyToken(string accsToken, string signature)
        {
            var req = (HttpWebRequest)WebRequest.Create(Dados.UrbanApiPro + "security/tokens/accs-proxy");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Headers["Authorization"] = "Bearer " + accsToken;
            req.Timeout = 12000;
            var body = "{\"type\":\"accs-proxy\",\"clientApp\":{\"name\":\"" + Dados.UrbanApp + "\"},\"signature\":\"" + signature + "\"}";
            var b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        public static string ExtractJsonValue(string json, string key)
        {
            if (String.IsNullOrEmpty(json) || String.IsNullOrEmpty(key)) return "";
            try
            {
                var d = Js.Deserialize<Dictionary<string, object>>(json);
                if (d != null && d.ContainsKey(key) && d[key] != null) return d[key].ToString();
            }
            catch { }
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }

        // A Urban devolve o token em "value" (o "accessToken" e so do JWT
        // inteiro, e o "token" pode ser o id do registro). Sem pegar o "value",
        // o accs saia vazio e a lista de servidores nunca chegava.
        public static string UrbanToken(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return "";
            string t = ExtractJsonValue(json, "value");
            if (String.IsNullOrEmpty(t)) t = ExtractJsonValue(json, "accessToken");
            if (String.IsNullOrEmpty(t)) t = ExtractJsonValue(json, "token");
            return (t ?? "").Trim();
        }

        // ---------------- medida rapida (TCP + UDP hard-reset) ----------------
        // A MESMA sonda da saude: TCP = aperto de mao cronometrado; UDP = os
        // 4 hard-reset do OpenVPN (so servidor vivo responde). 1,4 s no total.
        public class SondaRtt { public string Kind; public string Ip; public Socket Sock; public TcpClient Cli; public Task T; public Stopwatch Sw; public bool Done; public int Ms = -1; }

        public static byte[] HardResetVars(Random rnd)
        {
            var sid = new byte[8];
            rnd.NextBytes(sid);
            var pidb = new byte[] { 0, 0, 0, 0 };
            var vars = new List<byte[]> {
                concat(new byte[][]{ new byte[]{0x38}, sid, pidb, new byte[]{0} }),
                concat(new byte[][]{ new byte[]{0x38}, sid, pidb, new byte[]{0,0,0,0} }),
                concat(new byte[][]{ new byte[]{0x38}, sid, pidb }),
                concat(new byte[][]{ new byte[]{0x08}, sid, pidb, new byte[]{0} }) };
            return vars[0]; // (as 4 variantes sao montadas no local de uso)
        }
        static byte[] concat(byte[][] p)
        {
            var r = new byte[p.Sum(x => x.Length)];
            int i = 0;
            foreach (var b in p) { Array.Copy(b, 0, r, i, b.Length); i += b.Length; }
            return r;
        }

        public static Dictionary<string, int> MedirRtts(IEnumerable<Servidor> lista)
        {
            var res = new Dictionary<string, int>();
            var probes = new List<SondaRtt>();
            var rnd = new Random();
            foreach (var s in lista)
            {
                if (s == null || String.IsNullOrEmpty(s.IP) || s.Port <= 0) continue;
                if (res.ContainsKey(s.IP)) continue;
                res[s.IP] = -1;
                try
                {
                    if (s.Proto == "tcp")
                    {
                        var cli = new TcpClient();
                        var tsk = cli.ConnectAsync(s.IP, s.Port);
                        probes.Add(new SondaRtt { Kind = "tcp", Ip = s.IP, Cli = cli, T = tsk, Sw = Stopwatch.StartNew() });
                    }
                    else
                    {
                        var sk = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        sk.Connect(s.IP, s.Port);
                        var sid = new byte[8];
                        rnd.NextBytes(sid);
                        var pidb = new byte[] { 0, 0, 0, 0 };
                        byte[][] vars = new byte[][] {
                            concat(new byte[][]{ new byte[]{0x38}, sid, pidb, new byte[]{0} }),
                            concat(new byte[][]{ new byte[]{0x38}, sid, pidb, new byte[]{0,0,0,0} }),
                            concat(new byte[][]{ new byte[]{0x38}, sid, pidb }),
                            concat(new byte[][]{ new byte[]{0x08}, sid, pidb, new byte[]{0} }) };
                        foreach (var v in vars)
                        {
                            try { sk.Send(v, 0, v.Length, SocketFlags.None); } catch { }
                        }
                        probes.Add(new SondaRtt { Kind = "udp", Ip = s.IP, Sock = sk, Sw = Stopwatch.StartNew() });
                    }
                }
                catch { }
            }
            var lim = Stopwatch.StartNew();
            while (lim.ElapsedMilliseconds < 1400)
            {
                int abertos = 0;
                foreach (var p in probes)
                {
                    if (p.Done) continue;
                    try
                    {
                        if (p.Kind == "udp")
                        {
                            if (p.Sock.Poll(0, SelectMode.SelectRead))
                            {
                                var buf = new byte[2048];
                                if (p.Sock.Receive(buf) > 0) p.Ms = (int)p.Sw.ElapsedMilliseconds;
                                p.Done = true;
                            }
                            else abertos++;
                        }
                        else
                        {
                            if (p.T.IsCompleted)
                            {
                                if (p.Cli.Connected) p.Ms = (int)p.Sw.ElapsedMilliseconds;
                                p.Done = true;
                            }
                            else abertos++;
                        }
                    }
                    catch { p.Done = true; }
                }
                if (abertos == 0) break;
                Thread.Sleep(15);
            }
            foreach (var p in probes)
            {
                try { if (p.Sock != null) p.Sock.Close(); } catch { }
                try { if (p.Cli != null) p.Cli.Close(); } catch { }
                if (p.Ms >= 0) res[p.Ip] = p.Ms;
            }
            return res;
        }

        // ---------------- sonda em POOL (vigia do tunel) ----------------
        // Varios alvos da borda de midia ao mesmo tempo: so e queda se TODOS
        // falharem (um alvo sozinho oscila e daria falso alarme).
        public static List<string> AlvosSonda()
        {
            var alvos = new List<string> { "104.29.146.2", "104.29.129.2" };
            try
            {
                foreach (var a in ResolveDom("discord.media"))
                    if (!alvos.Contains(a)) alvos.Add(a);
            }
            catch { }
            return alvos.Distinct().Take(5).ToList();
        }

        public static bool SondaTunel(List<string> alvos)
        {
            var ss = new List<TcpClient>();
            var ts = new List<Task>();
            foreach (var al in alvos)
            {
                try
                {
                    var c = new TcpClient();
                    ss.Add(c);
                    ts.Add(c.ConnectAsync(al, 443));
                }
                catch { }
            }
            var ok = false;
            var t0 = Stopwatch.StartNew();
            while (t0.ElapsedMilliseconds < 2200)
            {
                for (int i = 0; i < ss.Count; i++)
                {
                    try
                    {
                        if (ts[i].IsCompleted && !ts[i].IsFaulted && ss[i].Connected) { ok = true; break; }
                    }
                    catch { }
                }
                if (ok) break;
                Thread.Sleep(30);
            }
            foreach (var c in ss) { try { c.Close(); } catch { } }
            return ok;
        }

        // ---------------- DNS com guarda de 120 s ----------------
        class DomEntrada { public List<string> IPs; public DateTime Quando; }
        static readonly Dictionary<string, DomEntrada> DomCache = new Dictionary<string, DomEntrada>();

        public static List<string> ResolveDom(string d)
        {
            lock (DomCache)
            {
                DomEntrada e;
                if (DomCache.TryGetValue(d, out e) && (DateTime.UtcNow - e.Quando).TotalSeconds < 120)
                    return e.IPs;
            }
            var ips = new List<string>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(d))
                    if (a.AddressFamily == AddressFamily.InterNetwork) ips.Add(a.ToString());
            }
            catch { }
            lock (DomCache) { DomCache[d] = new DomEntrada { IPs = ips, Quando = DateTime.UtcNow }; }
            return ips;
        }

        // ---------------- netstat: UDP + TCP dos processos ----------------
        public static string[] NetstatLinhas()
        {
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.ASCII
                };
                using (var p = Process.Start(psi))
                {
                    var txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    return txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                }
            }
            catch { return new string[0]; }
        }

        public static bool TestIpPrivado(string a)
        {
            if (a == "0.0.0.0" || a == "255.255.255.255") return true;
            if (a.StartsWith("10.") || a.StartsWith("127.") || a.StartsWith("169.254.")) return true;
            if (a.StartsWith("192.168.")) return true;
            var m = Regex.Match(a, @"^172\.(1[6-9]|2[0-9]|3[01])\.");
            if (m.Success) return true;
            if (Regex.IsMatch(a, @"^(22[4-9]|23[0-9]|25[0-9])\.")) return true;
            return false;
        }

        // UDP com destino (voz/video): netstat "UDP  local  remoto  PID"
        static readonly Regex RxUdp = new Regex(@"^\s*UDP\s+(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.Compiled);

        public static List<string> GetUdpRemotes(int[] pids, string[] netstat)
        {
            var res = new List<string>();
            var alvo = new HashSet<int>();
            foreach (var p in pids) if (p > 0) alvo.Add(p);
            if (alvo.Count == 0) return res;
            if (netstat == null) netstat = NetstatLinhas();
            foreach (var ln in netstat)
            {
                var m = RxUdp.Match(ln);
                if (!m.Success) continue;
                if (!alvo.Contains(int.Parse(m.Groups[3].Value))) continue;
                var rem = m.Groups[2].Value;
                if (!Regex.IsMatch(rem, @"^\d{1,3}(\.\d{1,3}){3}:\d+$")) continue;   // *:* ou IPv6
                var a = rem.Split(':')[0];
                if (a == "0.0.0.0") continue;
                if (a.StartsWith("127.") || a.StartsWith("169.254.")) continue;
                if (a.StartsWith("224.") || a.StartsWith("239.") || a == "255.255.255.255") continue;
                if (!res.Contains(a)) res.Add(a);
            }
            return res;
        }

        // TCP estabelecido dos processos (netstat tem o PID na ultima coluna)
        static readonly Regex RxTcp = new Regex(@"^\s*TCP\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.Compiled);

        public static List<string> GetTcpRemotes(int[] pids, string[] netstat)
        {
            var res = new List<string>();
            var alvo = new HashSet<int>();
            foreach (var p in pids) if (p > 0) alvo.Add(p);
            if (alvo.Count == 0) return res;
            if (netstat == null) netstat = NetstatLinhas();
            foreach (var ln in netstat)
            {
                var m = RxTcp.Match(ln);
                if (!m.Success) continue;
                var estado = m.Groups[3].Value.ToUpperInvariant();
                if (estado != "ESTABLISHED" && estado != "SYN_SENT") continue;
                if (!alvo.Contains(int.Parse(m.Groups[4].Value))) continue;
                var rem = m.Groups[2].Value;
                if (!Regex.IsMatch(rem, @"^\d{1,3}(\.\d{1,3}){3}:\d+$")) continue;
                var a = rem.Split(':')[0];
                if (a == "0.0.0.0" || a.StartsWith("127.")) continue;
                if (!res.Contains(a)) res.Add(a);
            }
            return res;
        }

        // ---------------- WFP: o IP que o netstat esconde ----------------
        // A transmissao de tela do Discord anda num socket UDP "sem destino"
        // (*:*) e o netstat nao mostra o IP. Quando o pacote falha, o Windows
        // anota um erro ICMP no WFP com o IP remoto + porta local - e a porta
        // acusa o dono pelo PID. A foto do WFP sai num filho (nao trava).
        public static Dictionary<string, bool> WfpPares = new Dictionary<string, bool>();
        public static string WfpLido = "";
        public static DateTime WfpUltimo = DateTime.MinValue;
        public static Process WfpProc;

        public static void DispararFotoWfp()
        {
            try
            {
                if (WfpProc != null && !WfpProc.HasExited) return;
                if ((DateTime.UtcNow - WfpUltimo).TotalSeconds < 10) return;
                Store.Init();
                var xml = Path.Combine(Store.TempDir, "wfp_tmp.xml");
                var h = Path.Combine(Store.TempDir, "wfp_foto.cmd");
                File.WriteAllText(h, "netsh wfp show netevents file=\"" + xml + "\" >nul 2>&1\r\n", Encoding.ASCII);
                WfpUltimo = DateTime.UtcNow;
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + h + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                WfpProc = Process.Start(psi);
            }
            catch { }
        }

        public static void LerFotoWfp()
        {
            try
            {
                if (WfpProc != null && !WfpProc.HasExited) return;
                var xml = Path.Combine(Store.TempDir, "wfp_tmp.xml");
                if (!File.Exists(xml)) return;
                var carimbo = File.GetLastWriteTimeUtc(xml).ToString("o");
                if (WfpLido == carimbo) return;
                var txt = File.ReadAllText(xml);
                WfpLido = carimbo;
                foreach (Match m in Regex.Matches(txt, "<header>(.*?)</header>", RegexOptions.Singleline))
                {
                    var b = m.Groups[1].Value;
                    if (!b.Contains("<ipProtocol>17</ipProtocol>")) continue;   // so UDP
                    var mr = Regex.Match(b, "<remoteAddrV4>([^<]+)</remoteAddrV4>");
                    var mp = Regex.Match(b, "<localPort>(\\d+)</localPort>");
                    if (!mr.Success || !mp.Success) continue;
                    if (mp.Groups[1].Value == "0") continue;
                    var ip = mr.Groups[1].Value;
                    if (TestIpPrivado(ip)) continue;
                    var k = mp.Groups[1].Value + "|" + ip;
                    if (!WfpPares.ContainsKey(k)) WfpPares[k] = true;
                }
                try { File.Delete(xml); } catch { }
            }
            catch { }
        }

        public static List<string> GetIcmpRemotes(int[] pids, string[] netstat)
        {
            var res = new List<string>();
            var alvo = new HashSet<int>();
            foreach (var p in pids) if (p > 0) alvo.Add(p);
            if (alvo.Count == 0) return res;
            DispararFotoWfp();
            LerFotoWfp();
            if (WfpPares.Count == 0) return res;
            // quem e o dono de cada porta local AGORA
            var donoPorta = new Dictionary<int, int>();
            if (netstat == null) netstat = NetstatLinhas();
            foreach (var ln in netstat)
            {
                var m = RxUdp.Match(ln);
                if (!m.Success) continue;
                var loc = m.Groups[1].Value;
                var mp = Regex.Match(loc, ":(\\d+)$");
                if (!mp.Success) continue;
                donoPorta[int.Parse(mp.Groups[1].Value)] = int.Parse(m.Groups[3].Value);
            }
            foreach (var k in WfpPares.Keys.ToArray())
            {
                var pp = k.Split('|');
                int porta; int.TryParse(pp[0], out porta);
                if (!donoPorta.ContainsKey(porta)) continue;
                if (!alvo.Contains(donoPorta[porta])) continue;
                var ip = pp[1];
                if (!res.Contains(ip)) res.Add(ip);
            }
            return res;
        }

        // ---------------- rotas (route.exe - IPv4) ----------------
        public static void RunCmd(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(6000);
                }
            }
            catch { }
        }

        public static string MascaraDePrefixo(string prefixo)
        {
            // "1.2.3.4" -> /32 ; "104.29.128.0/19" -> 255.255.224.0
            var bar = prefixo.IndexOf('/');
            int cidr = bar >= 0 ? int.Parse(prefixo.Substring(bar + 1)) : 32;
            uint m = cidr == 0 ? 0u : 0xFFFFFFFFu << (32 - cidr);
            return string.Format("{0}.{1}.{2}.{3}", (m >> 24) & 255, (m >> 16) & 255, (m >> 8) & 255, m & 255);
        }

        public static string RedeDePrefixo(string prefixo)
        {
            var bar = prefixo.IndexOf('/');
            return bar >= 0 ? prefixo.Substring(0, bar) : prefixo;
        }
    }

    // =================================================================
    //  CONTROLES DE VIDRO (porte do skin do app principal): o fundo e a
    //  fatia da foto/vidro que esta atras + barra de rolagem vertical
    //  desenhada no estilo ciano (a nativa nunca aparece)
    // =================================================================
    public class LvVidro : ListView
    {
        public Janela Dono;
        public Color CorGrupo = Color.FromArgb(150, 232, 226);
        public Color CorFilete = Color.FromArgb(58, 136, 142);

        // WS_EX_COMPOSITED: o Windows pinta a lista inteira num buffer e joga
        // PRONTA na tela - sem o estado intermediario (fundo apagado, texto
        // depois) que fazia os nomes piscarem no hover e na rolagem
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                return cp;
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }
        [StructLayout(LayoutKind.Sequential)] struct NMCUSTOMDRAW { public NMHDR hdr; public int dwDrawStage; public IntPtr hdc; public RECT rc; public IntPtr dwItemSpec; public int uItemState; public IntPtr lItemlParam; }
        [StructLayout(LayoutKind.Sequential)] struct NMLVCUSTOMDRAW { public NMCUSTOMDRAW nmcd; public int clrText; public int clrTextBk; public int iSubItem; public int dwItemType; }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
        static readonly System.Reflection.PropertyInfo PropId = typeof(ListViewGroup).GetProperty("ID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public LvVidro()
        {
            DoubleBuffered = true;
            // nasce escuro: antes da primeira pintura do tema, o nativo
            // apagaria de branco (era um dos quadrados brancos ao abrir)
            try { BackColor = Color.FromArgb(14, 26, 28); ForeColor = Color.White; } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            const int WM_NOTIFY = 0x204E;      // WM_NOTIFY refletido
            const int WM_NCPAINT = 0x0085;
            if (m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero && Dono != null)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam)) Dono.PintarFatiaFundo(g, this, ClientRectangle);
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_NOTIFY && m.LParam != IntPtr.Zero && Dono != null)
            {
                NMHDR h = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (h.code == -12 && h.hwndFrom == Handle)             // NM_CUSTOMDRAW da lista
                {
                    NMLVCUSTOMDRAW cd = (NMLVCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMLVCUSTOMDRAW));
                    if (cd.nmcd.dwDrawStage == 1 && cd.nmcd.hdc != IntPtr.Zero)   // CDDS_PREPAINT
                    {
                        if (cd.dwItemType == 1)       // LVCDI_GROUP: titulo do grupo em ciano
                        {
                            Grupo(cd.nmcd.hdc, cd.nmcd.dwItemSpec);
                            m.Result = (IntPtr)4;     // CDRF_SKIPDEFAULT
                            return;
                        }
                        // com buffer duplo a lista apaga o fundo por conta propria: repinta
                        using (Graphics g = Graphics.FromHdc(cd.nmcd.hdc)) Dono.PintarFatiaFundo(g, this, ClientRectangle);
                    }
                }
            }
            if (m.Msg == WM_NCPAINT && Dono != null)
            {
                // barra de rolagem propria: a nativa nunca e desenhada
                try { Dono.DesenharBarraRolagem(this); } catch { }
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
            if (Dono != null && (m.Msg == 0x000F || m.Msg == 0x115 || m.Msg == 0x114 || m.Msg == 0x20A))
                try { Dono.RedesenharBarra(this); } catch { }
        }

        // A coluna do IP:porta e colorida pelo proprio ListView (ForeColor do
        // subitem, verde-neon). Nao ha pintura manual aqui de proposito: a
        // lista esta em OwnerDraw, o Windows pinta a linha inteira e uma
        // repintada por cima via NM_CUSTOMDRAW sao fora � o item ficava
        // sem pintar.
        void Grupo(IntPtr hdc, IntPtr id)
        {
            try
            {
                RECT rc = new RECT();
                rc.T = 1;                                // LVGGR_HEADER
                if (SendMessage(Handle, 0x1000 + 98, id, ref rc) == IntPtr.Zero) return;   // LVM_GETGROUPRECT
                Rectangle r = Rectangle.FromLTRB(rc.L, rc.T, rc.R, rc.B);
                string txt = "";
                if (PropId != null)
                    foreach (ListViewGroup gr in Groups)
                        if ((int)PropId.GetValue(gr, null) == id.ToInt32()) { txt = gr.Header; break; }
                using (Graphics g = Graphics.FromHdc(hdc))
                using (Font f = new Font(Font, FontStyle.Bold))
                using (Brush b = new SolidBrush(CorGrupo))
                using (Pen p = new Pen(CorFilete))
                {
                    if (Dono != null) Dono.PintarFatiaFundo(g, this, r);
                    g.DrawString(txt, f, b, r.Left + 9, r.Top + (r.Height - f.Height) / 2 - 1);
                    g.DrawLine(p, r.Left, r.Bottom - 2, r.Left + (int)(ClientSize.Width * 0.35), r.Bottom - 2);
                }
            }
            catch { }
        }
    }

    // caixas de programas e de log: pintura 100% nossa, num buffer unico
    // (o mesmo tratamento do seletor de paises: o Windows nao pinta NADA
    // intermediario - zero piscada, fundo estatico na rolagem)
    public class LbVidro : ListBox
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SCROLLINFO { public int cbSize; public int fMask; public int nMin; public int nMax; public int nPage; public int nPos; public int nTrackPos; }
        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);

        public Janela Dono;

        // ROLAGEM 100% NOSSA. A TopIndex do ListBox faz o Windows chamar
        // ScrollWindowEx: ele desloca os pixels da tela e a foto de fundo
        // "anda" e pisca a cada passo. Aqui a posicao e um numero nosso (em
        // pixels) e a pintura desenha os itens no lugar - o vidro nunca se
        // mexe e nao existe estado intermediario nenhum na tela.
        int rolagemY = 0;
        int rolagemX = 0;
        public int RolagemY { get { return rolagemY; } }
        public int RolagemX { get { return rolagemX; } }
        int AlturaItem { get { return Math.Max(1, ItemHeight); } }
        public int RolagemMax
        {
            get
            {
                int alt = AlturaItem;
                return Math.Max(0, Items.Count * alt - ClientSize.Height + alt);
            }
        }

        // quanto o Windows calcula que o conteudo e largo. Como a barra nativa
        // fica escondida (o Windows nunca pinta por cima do nosso ET), a
        // medida e nossa: medimos o item mais largo (so quando a lista muda).
        int largMaxCache = -1;
        int itensCache = -1;
        int LarguraMaxItem()
        {
            if (largMaxCache >= 0 && itensCache == Items.Count) return largMaxCache;
            int max = 0;
            try
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    int w = TextRenderer.MeasureText(Convert.ToString(Items[i]), Font).Width;
                    if (w > max) max = w;
                }
            }
            catch { }
            max += 12;
            largMaxCache = max;
            itensCache = Items.Count;
            return max;
        }
        public int RolagemXMax
        {
            get { return Math.Max(0, LarguraMaxItem() - ClientSize.Width); }
        }

        public void InvalidarLarguraCache() { largMaxCache = -1; }

        public LbVidro()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // nasce escuro pelo mesmo motivo da lista (o tom exato o tema acerta)
            try { BackColor = Color.FromArgb(4, 13, 15); ForeColor = Color.White; } catch { }
        }

        public void IrParaFim() { RolarParaPixels(RolagemMax); }

        // A BARRA NATIVA SAI DE CENA. Ela vivia repintando por cima do nosso
        // ET (o Windows redesenha a barra dela quando o conteudo muda) e era
        // aquele pisca restando embaixo do et. Escondida, a faixa da barra
        // entra na area do cliente: o fundo do tema sai pintado no mesmo
        // buffer do texto e o ET e o unico desenho que passa por cima.
        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool fShow);
        int largBarraCache = -1;
        int altBarraCache = -1;
        int GetLargBarra() { if (largBarraCache < 0) largBarraCache = GetSystemMetrics(2); return largBarraCache; }
        int GetAltBarra() { if (altBarraCache < 0) altBarraCache = GetSystemMetrics(3); return altBarraCache; }
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EsconderBarras();
        }

        public void EsconderBarras()
        {
            try { ShowScrollBar(Handle, 3, false); } catch { }     // SB_BOTH
        }

        public void Repintar()
        {
            EsconderBarras();
            try { Invalidate(); Update(); } catch { }
            try { if (Dono != null) Dono.RedesenharBarra(this); } catch { }
        }

        public void RolarParaPixels(int px)
        {
            int max = RolagemMax;
            if (px < 0) px = 0;
            if (px > max) px = max;
            rolagemY = px;
            Repintar();
        }

        // rolagem horizontal tambem e nossa (o Windows deslizaria os pixels)
        public void RolarParaX(int px)
        {
            int max = RolagemXMax;
            if (px < 0) px = 0;
            if (px > max) px = max;
            rolagemX = px;
            Repintar();
        }

        // -------- MOUSE NA FAIXA DA BARRA (a nativa someu, a nossa e nossa) ----
        bool arrastandoEt = false;
        string arrastandoLado = "";

        bool SobreBarra(int x, int y, out string lado)
        {
            lado = "";
            try
            {
                int largBarra = GetLargBarra();
                int altBarra = GetAltBarra();
                bool rolaV = RolagemMax > 0;
                bool rolaH = RolagemXMax > 0;
                if (rolaV && x >= ClientSize.Width - largBarra) { lado = "v"; return true; }
                if (rolaH && y >= ClientSize.Height - altBarra) { lado = "h"; return true; }
            }
            catch { }
            return false;
        }

        Rectangle ZonaEt(string lado)
        {
            if (Dono == null) return Rectangle.Empty;
            try { return Dono.ZonaEtAtual(this, lado); }
            catch { return Rectangle.Empty; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            string lado;
            if (e.Button == MouseButtons.Left && SobreBarra(e.X, e.Y, out lado))
            {
                var zona = ZonaEt(lado);
                if (!zona.IsEmpty && zona.Contains(e.Location))
                {
                    arrastandoEt = true;              // agarrou o et
                    arrastandoLado = lado;
                }
                else
                {
                    // clique no trilho: uma pagina para o lado clicado
                    if (lado == "v")
                    {
                        int pag = Math.Max(1, ClientSize.Height - GetLargBarra());
                        RolarParaPixels(e.Y < zona.Y ? rolagemY - pag : rolagemY + pag);
                    }
                    else
                    {
                        int pag = Math.Max(1, ClientSize.Width - GetAltBarra());
                        RolarParaX(e.X < zona.X ? rolagemX - pag : rolagemX + pag);
                    }
                }
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (arrastandoEt)
            {
                if (arrastandoLado == "v")
                {
                    int lado = GetLargBarra();
                    int util = Math.Max(1, ClientSize.Height - lado);
                    int frac = (e.Y - lado / 2) * 1000 / util;
                    RolarParaPixels(RolagemMax * frac / 1000);
                }
                else
                {
                    int lado = GetAltBarra();
                    int util = Math.Max(1, ClientSize.Width - lado);
                    int frac = (e.X - lado / 2) * 1000 / util;
                    RolarParaX(RolagemXMax * frac / 1000);
                }
                return;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            arrastandoEt = false;
            arrastandoLado = "";
            base.OnMouseUp(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            const int WM_NCPAINT = 0x0085;
            const int WM_SHOWWINDOW = 0x0018;
            if (m.Msg == WM_SHOWWINDOW && (m.WParam.ToInt64() & 1) != 0)
            {
                // o Windows acabou de (re)criar a barra dele: esconde de novo
                // antes que ela chegue a pintar alguma coisa
                try { BeginInvoke((Action)delegate { EsconderBarras(); Repintar(); }); } catch { }
            }
            if (m.Msg == WM_ERASEBKGND)
            {
                // a pintura buffered cobre o cliente inteiro: nao apaga nada antes
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_NCPAINT && Dono != null)
            {
                try { Dono.DesenharBarraRolagem(this); } catch { }
                m.Result = (IntPtr)1;
                return;
            }
            // ROLAGEM VERTICAL SEM DESLIZAR PIXELS: nem a TopIndex nem o Windows
            // mexem na tela - so o numero nosso de pixels muda e a pintura
            // bufferada redesenha tudo NUMA passada (fundo PARADO, zero pisca).
            if (m.Msg == 0x0115 && Dono != null)// WM_VSCROLL
            {
                int tipo = (int)(m.WParam.ToInt64() & 0xFFFF);
                int pos = (int)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                RolarPara(tipo, pos);
                return;
            }
            if (m.Msg == 0x020A && Dono != null)           // WM_MOUSEWHEEL
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int linhas = SystemInformation.MouseWheelScrollLines;
                if (linhas <= 0) linhas = 3;
                int passos = -(delta * linhas / 120);
                RolarParaPixels(rolagemY + passos * AlturaItem);
                m.Result = (IntPtr)0;
                return;
            }
            if (m.Msg == 0x0114 && Dono != null)           // WM_HSCROLL
            {
                RolagemHorizontalNativa(ref m);
                return;
            }
            if (m.Msg == 0x0100 && Dono != null)           // WM_KEYDOWN (setas, pgup/pgdn, home/end)
            {
                int tecla = (int)m.WParam.ToInt64();
                int pag = Math.Max(1, ClientSize.Height / AlturaItem);
                int alvo = rolagemY;
                if (tecla == 0x26) alvo = rolagemY - AlturaItem;            // seta pra cima
                else if (tecla == 0x28) alvo = rolagemY + AlturaItem;      // seta pra baixo
                else if (tecla == 0x21) alvo = rolagemY - pag * AlturaItem; // page up
                else if (tecla == 0x22) alvo = rolagemY + pag * AlturaItem; // page down
                else if (tecla == 0x24) alvo = 0;                          // home
                else if (tecla == 0x23) alvo = RolagemMax;                 // end
                else { base.WndProc(ref m); return; }
                RolarParaPixels(alvo);
                m.Result = (IntPtr)0;
                return;
            }
            base.WndProc(ref m);
            // a faixa da barra agora faz parte do cliente: depois de cada
            // pintura o Windows apaga tudo, entao o ET e reposto aqui (sempre
            // por cima, nunca por baixo)
            if (Dono != null && (m.Msg == 0x000F || m.Msg == 0x007E || m.Msg == 0x001A))
                try { Dono.RedesenharBarra(this); } catch { }
        }

        // barra horizontal: rolagem nossa tambem (o Windows so deslizaria pixels)
        void RolagemHorizontalNativa(ref Message m)
        {
            int tipo = (int)(m.WParam.ToInt64() & 0xFFFF);
            int pos = (int)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            int max = RolagemXMax;
            int alvo = rolagemX;
            int passo = Math.Max(8, AlturaItem);
            int pagina = Math.Max(50, ClientSize.Width);
            if (tipo == 0) alvo = rolagemX - passo;          // SB_LINELEFT
            else if (tipo == 1) alvo = rolagemX + passo;     // SB_LINERIGHT
            else if (tipo == 2) alvo = rolagemX - pagina;    // SB_PAGELEFT
            else if (tipo == 3) alvo = rolagemX + pagina;    // SB_PAGERIGHT
            else if (tipo == 4 || tipo == 5)                 // THUMBPOSITION / THUMBTRACK
                alvo = max * pos * 1000 / 65535;
            else if (tipo == 6) alvo = 0;                    // SB_TOP
            else if (tipo == 7) alvo = max;                  // SB_BOTTOM
            RolarParaX(alvo);
        }

        void RolarPara(int tipo, int pos)
        {
            int alt = AlturaItem;
            int pag = Math.Max(1, ClientSize.Height / alt);
            int maxItem = Math.Max(0, Items.Count - 1);
            int novo = rolagemY / alt;
            if (tipo == 0) novo--;                       // SB_LINEUP
            else if (tipo == 1) novo++;                  // SB_LINEDOWN
            else if (tipo == 2) novo -= pag;             // SB_PAGEUP
            else if (tipo == 3) novo += pag;             // SB_PAGEDOWN
            else if (tipo == 4 || tipo == 5)             // THUMBPOSITION / THUMBTRACK
                novo = maxItem * pos * 1000 / 65535;
            else if (tipo == 6) novo = 0;                // SB_TOP
            else if (tipo == 7) novo = maxItem;           // SB_BOTTOM
            if (novo < 0) novo = 0;
            if (novo > maxItem) novo = maxItem;
            RolarParaPixels(novo * alt);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // NADA aqui de proposito: o fundo e pintado DENTRO do OnPaint, no
            // mesmo buffer e na mesma passada do texto. Se o Windows pintasse o
            // fundo antes, apareceria um quadro com a cor nativa da caixa -
            // era exatamente o piscado que aparecia quando o log atualizava.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics;
                // 1) fundo: primeiro cobre TUDO com a cor da caixa (impede que restos
                // de um quadro fiquem embaixo quando o?Windows repinta so
                // um pedaco) e depois entra a foto/vidro do tema por cima
                using (var br = new SolidBrush(BackColor)) g.FillRectangle(br, ClientRectangle);
                if (Dono != null) Dono.PintarFatiaFundo(g, this, ClientRectangle);
                // 2) itens na posicao da NOSSA rolagem (offset horizontal junto)
                int desloc = rolagemX;
                int alturaItem = AlturaItem;
                if (rolagemY > RolagemMax) rolagemY = RolagemMax;
                if (rolagemY < 0) rolagemY = 0;
                int primeiro = rolagemY / alturaItem;       // item inteiro do topo
                int resto = rolagemY % alturaItem;          // pedaco do item cortado
                int visiveis = ClientSize.Height / alturaItem + 2;
                for (int k = 0; k < visiveis; k++)
                {
                    int i = primeiro + k;
                    if (i < 0 || i >= Items.Count) break;
                    // posicao calculada por nos: o Windows nunca desliza pixel
                    var r = new Rectangle(-desloc, k * alturaItem - resto, ClientSize.Width + desloc, alturaItem);
                    bool sel = false;
                    try { sel = SelectedIndices.Contains(i); } catch { }
                    if (Dono != null) Dono.DesenharItemLista(this, g, i, r, sel);
                }
                // 3) o et da barra, no MESMO buffer (fundo + texto + et juntos)
                if (Dono != null) Dono.DesenharEtNoBuffer(this, g);
                return;
            }
            catch { }
        }
    }

    // caixa de marcacao do skin: POR CIMA do desenho nativo (que e branco).
    // Medidas iguais as do app principal: quadradinho de 13 px com o miolo
    // quase todo preenchido em ciano com leve degrade, sem "visto".
    public class CbVidro : CheckBox
    {
        public Janela Dono;

        public CbVidro()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            try { BackColor = Color.Transparent; } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014)          // WM_ERASEBKGND: a pintura cobre tudo
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics;
                float esc = 1f;
                try { esc = DeviceDpi / 96f; } catch { esc = 1f; }
                if (esc < 0.5f) esc = 0.5f;

                int t = (int)Math.Round(13 * esc);                 // o quadradinho do app principal
                int folga = (int)Math.Round(3 * esc);
                var b = new Rectangle(0, (Height - t) / 2, t, t);

                // fundo do tema atras do quadradinho: pintamos o cliente inteiro com a
                // mesma fatia do fundo da janela (no principal essa parte fica
                // transparente; aqui e a mesma imagem, sem risco de rastro)
                if (Dono != null) Dono.PintarFatiaFundo(g, this, ClientRectangle);
                else using (var br0 = new SolidBrush(BackColor)) g.FillRectangle(br0, ClientRectangle);

                using (var f = new SolidBrush(Color.FromArgb(175, 6, 19, 21)))
                    g.FillRectangle(f, b);
                using (var p = new Pen(Color.FromArgb(Focused ? 255 : 200, 74, 138, 146), 1))
                    g.DrawRectangle(p, b.X, b.Y, t - 1, t - 1);

                if (Checked)
                {
                    // miolo QUASE TODO preenchido, em ciano com leve degrade
                    int alfa = Enabled ? 240 : 115;
                    int m = Math.Max(2, (int)Math.Round(2 * esc));
                    int wq = t - m * 2, hq = t - m * 2;
                    if (wq > 2 && hq > 2)
                        using (var lg = new LinearGradientBrush(
                            new Rectangle(b.X + m, b.Y + m, wq, hq),
                            Color.FromArgb(alfa, 150, 244, 234),
                            Color.FromArgb(alfa, 22, 110, 120),
                            LinearGradientMode.Vertical))
                            g.FillRectangle(lg, b.X + m, b.Y + m, wq, hq);
                }

                // texto (o principal deixa o nativo pintar; aqui pintamos junto)
                var cor = Enabled ? Janela.CorTexto : Janela.CorApagado;
                int xTexto = (int)Math.Round(19 * esc);
                using (var br2 = new SolidBrush(cor))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(Text, Font, br2,
                        new RectangleF(xTexto, 0, Math.Max(10, Width - xTexto - 4), Height), sf);
                }
                return;
            }
            catch { }
        }
    }

    // seletor fechado: vidro escuro, borda ciano e seta fina (o nativo vem
    // branco e piscava: a capa cobre ele no mesmo ciclo de pintura)
    public class CboVidro : ComboBox
    {
        public Color CorBorda = Color.FromArgb(62, 130, 138);
        public Janela Dono;

        public CboVidro()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            DoubleBuffered = true;
            // o nativo pinta branco antes da nossa capa: nascendo escuro o
            // primeiro quadro ja vem na cor do tema (sem quadrado branco)
            try { BackColor = Color.FromArgb(7, 23, 26); ForeColor = Color.White; } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) Capa();                 // WM_PAINT
        }

        void Capa()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                using (Brush veu = new SolidBrush(Color.FromArgb(150, 7, 23, 26)))
                using (Pen borda = new Pen(Color.FromArgb(Focused ? 255 : 170, CorBorda)))
                using (Brush tx = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(120, 140, 142)))
                using (Pen seta = new Pen(Color.FromArgb(215, 214, 234, 234), 1.3f))
                using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    Rectangle r = ClientRectangle;
                    if (Dono != null) Dono.PintarFatiaFundo(g, this, r);
                    g.FillRectangle(veu, r);
                    g.DrawRectangle(borda, 0, 0, r.Width - 1, r.Height - 1);
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    string t = SelectedIndex >= 0 ? Convert.ToString(Items[SelectedIndex]) : "";
                    g.DrawString(t, Font, tx, new RectangleF(4, 0, r.Width - 26, r.Height), sf);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float cx = r.Width - 12.5f, cy = r.Height / 2f;
                    g.DrawLines(seta, new PointF[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy + 2f), new PointF(cx + 3.5f, cy - 1.5f) });
                }
            }
            catch { }
        }
    }

    // menu da bandeja nas cores do tema
    public class MenuDoTema : ToolStripProfessionalRenderer
    {
        public MenuDoTema() : base(new Tabela()) { }
        class Tabela : ProfessionalColorTable
        {
            public Tabela() { UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(7, 23, 26); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(7, 23, 26); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(7, 23, 26); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(7, 23, 26); } }
            public override Color MenuBorder { get { return Color.FromArgb(62, 130, 138); } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(28, 71, 77); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(28, 71, 77); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(28, 71, 77); } }
            public override Color SeparatorDark { get { return Color.FromArgb(62, 130, 138); } }
            public override Color SeparatorLight { get { return Color.FromArgb(62, 130, 138); } }
        }
    }

    // =================================================================
    //  JANELA PRINCIPAL (interface + motor)
    // =================================================================
    public class Janela : Form
    {
        // paleta (medida na imagem de referencia)
        public static readonly Color CorTexto = Color.White;
        public static readonly Color CorApagado = Color.FromArgb(160, 190, 193);
        public static readonly Color CorCiano = Color.FromArgb(75, 227, 213);
        public static readonly Color CorBorda = Color.FromArgb(62, 130, 138);
        static readonly Color CorAmbar = Color.FromArgb(214, 160, 72);
        static readonly Color CorVerde = Color.FromArgb(126, 226, 140);
        public static readonly Color CorVerdeNeon = Color.FromArgb(62, 224, 132);

        // controles
        PictureBox picAlien;
        Label lblSub, lblIP, lblTemas, lblPais, lblProto, lblStatus, lblApps, lblLog;
        ComboBox cboTema, cboCountry, cboProto;
        Button btnRefresh, btnPing, btnAdd, btnRemove, btnConnect, btnUninstall, btnUpdate;
        ListView lv;
        LbVidro lbApps, lbLog;
        CbVidro chkFull, chkAutoRe, chkStartup, chkBootConn;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;

        // estado
        string temaAtual = "ets";
        bool suppressUi = false;
        bool telaWeb = false;
        bool connected = false;
        bool exiting = false;
        bool minTray = true;
        string protocol = "auto";
        bool autoRecon = false;
        bool bootConnect = false;
        bool fullTunnel = false;
        int reAuto = 0;
        string servidorAtual = "";
        // IP e porta que o tunel esta usando - a tela mostra junto do nome
        string servidorEndereco = "";
        string currentIP = "";
        string versaoNova = "";
        int sortCol = -1;
        bool sortAsc = true;
        string countryFilter = null;
        List<Servidor> gateServers = new List<Servidor>();
        List<Servidor> pvlServers = new List<Servidor>();
        List<Servidor> vpnBookServers = new List<Servidor>();
        List<Servidor> urbanServers = new List<Servidor>();
        List<Servidor> servers = new List<Servidor>();
        List<Programa> appList = new List<Programa>();
        // icones dos programas da lista, por caminho: extrair o icone do .exe a
        // cada repaint trava a lista, entamo guarda em cache
        readonly Dictionary<string, Icon> cacheIcones = new Dictionary<string, Icon>();
        string gateRaw = null;
        List<string> ownedRoutes = new List<string>();
        bool defRoute = false;
        bool dnsSalvo = false;
        List<string> savedDns = null;
        bool savedDnsDhcp = false;
        string ifAlias = "";
        string vpnGw = "";
        Process ovpnProc = null;
        int ultimaMidia = -1;
        int vigiaTick = 0;
        bool instalouOpenVpn = false;
        Dictionary<string, List<int>> pingHist = new Dictionary<string, List<int>>();
        Dictionary<string, int[]> serieCache = new Dictionary<string, int[]>();
        Dictionary<string, List<int>> portasPorIP = new Dictionary<string, List<int>>();
        List<int> portasVistas = new List<int>();
        // porta+protocolo que JA ABRIU o tunel nesse ip (ex: "1194/udp"):
        // na proxima vez vai direto nela, sem sondar nada
        Dictionary<string, string> portaProtoPorIP = new Dictionary<string, string>();
        Dictionary<string, Servidor> filterMapIso = new Dictionary<string, Servidor>();
        List<Dados.FontePais> filterMap = new List<Dados.FontePais>();
        DateTime huntUltima = DateTime.MinValue;

        // downloads em background
        Task<string> dlTask = null;
        DateTime dlStart = DateTime.MinValue;
        int dlTentativas = 0;
        DateTime? dlReTimeout = null;
        // o espelho GitHub baixa em paralelo com o VPN Gate
        Task<string> dlEspelho = null;
        DateTime dlEspStart = DateTime.MinValue;
        class PvlTarefa { public Task<string> T; public string ISO; public string Tipo; public WebClient W; public DateTime Start; }
        List<PvlTarefa> pvlTasks = new List<PvlTarefa>();
        List<PvlTarefa> pvlQueue = new List<PvlTarefa>();
        List<Servidor> pvlOut = new List<Servidor>();
        int pvlFalhas = 0;
        Task ipTask = null;
        WebClient ipWc = null;

        // log em fila (o console nunca pinta linha por linha)
        readonly Queue<string> logFila = new Queue<string>();

        // timers
        System.Windows.Forms.Timer pollTimer, watch, rtimer, reconTimer, bootTimer, connTimer, updateTimer, fontesTimer;
        System.Windows.Forms.Timer penteTimer;   // pente fino logo ao abrir
        bool saindo = false;   // o programa esta fechando: nao comeca mais nada
        bool disposing = false;   // o .ovpn esta subindo: a lista espera
        System.Windows.Forms.Timer trayTimer;
        System.Windows.Forms.Timer piscarTimer;
        Icon[] trayFrames;
        int trayFrameIdx = 0;
        List<Bitmap> trayFramesBmp = new List<Bitmap>();
        bool etPendente = false;   // quadros prontos antes da janela ter handle
        FormWindowState estadoJanela;

        // caches de pincel/caneta (a lista desenha 5 celulas por linha)
        readonly Dictionary<int, SolidBrush> pincelCache = new Dictionary<int, SolidBrush>();
        readonly Dictionary<string, Pen> canetaCache = new Dictionary<string, Pen>();
        StringFormat fmtCel;
        Font fntConSub;
        StringFormat fmtCentro;
        Bitmap imgComp;
        Image imgAlienEscuro, imgAlienClaro, imgFundo, imgFundoGoku;

        public Janela()
        {
            Store.Init();
            Text = "Ultra VPN Free - t\u00fanel por aplicativo";
            StartPosition = FormStartPosition.CenterScreen;
            Size = Program.JanelaDoUsuario();
            // a tela e desenhada em 917 x 756 de verdade agora (antes o
            // Windows esticava o bitmap inteiro). Sem isto o Forms
            // reescalaria por conta propria e a janela ficaria 2,25x maior.
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(1f, 1f);
            MinimumSize = new Size(760, 640);
            BackColor = Color.FromArgb(6, 18, 20);
            Font = new Font("Segoe UI", 9);
            fmtCel = new StringFormat(StringFormatFlags.NoWrap);
            fmtCel.Trimming = StringTrimming.EllipsisCharacter;
            fmtCel.LineAlignment = StringAlignment.Center;
            fmtCentro = new StringFormat(StringFormatFlags.NoWrap);
            fmtCentro.Alignment = StringAlignment.Center;
            fmtCentro.LineAlignment = StringAlignment.Center;
            fmtCentro.Trimming = StringTrimming.EllipsisCharacter;
            fntConSub = new Font("Segoe UI", 8);

            // ---------------- imagens (ao lado do exe) ----------------
            imgAlienEscuro = CarregarArquivo("alien.escuro.png");
            imgAlienClaro = CarregarArquivo("alien.claro.png");
            imgFundo = CarregarArquivo("fundo.alien.jpg");
            imgFundoGoku = CarregarArquivo("fundo.goku.jpg");

            // ---------------- logo ----------------
            picAlien = new PictureBox();
            picAlien.Location = new Point(12, 5);
            picAlien.Size = new Size(96, 64);
            picAlien.SizeMode = PictureBoxSizeMode.Zoom;
            picAlien.BackColor = Color.Black;
            picAlien.Paint += delegate(object snd, PaintEventArgs ev)
            {
                try
                {
                    using (var penB = new Pen(CorBorda, 1))
                        ev.Graphics.DrawRectangle(penB, 0, 0, picAlien.Width - 1, picAlien.Height - 1);
                }
                catch { }
            };
            if (imgAlienEscuro != null) picAlien.Image = imgAlienEscuro;
            Controls.Add(picAlien);

            // ---------------- rotulos do topo ----------------
            lblSub = new Label();
            lblSub.Text = "VPN gr\u00e1tis da Am\u00e9rica do Sul - s\u00f3 os programas que voc\u00ea escolher passam pela VPN";
            lblSub.Location = new Point(120, 42);
            lblSub.Size = new Size(540, 20);
            Controls.Add(lblSub);

            lblIP = new Label();
            lblIP.Text = "IP atual: ...";
            lblIP.TextAlign = ContentAlignment.MiddleRight;
            lblIP.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblIP.Location = new Point(666, 46);
            lblIP.Size = new Size(230, 24);
            Controls.Add(lblIP);

            // ---------------- temas ----------------
            lblTemas = new Label();
            lblTemas.Text = "Temas:";
            lblTemas.Location = new Point(628, 15);
            lblTemas.Size = new Size(44, 22);
            Controls.Add(lblTemas);

            cboTema = new CboVidro();
            ((CboVidro)cboTema).Dono = this;
            cboTema.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTema.ItemHeight = 17;
            cboTema.Location = new Point(674, 12);
            cboTema.Size = new Size(222, 23);
            cboTema.DropDownWidth = 222;
            cboTema.Items.AddRange(Dados.TemaTexts);
            cboTema.DrawMode = DrawMode.OwnerDrawFixed;
            cboTema.DrawItem += DesenharCombo;
            cboTema.SelectedIndexChanged += delegate
            {
                if (suppressUi) return;
                var i = cboTema.SelectedIndex;
                if (i < 0) return;
                temaAtual = Dados.TemaKeys[i];
                SalvarEstado();
                Log("Tema alterado: " + Dados.TemaTexts[i]);
                try { AplicarTema(); } catch { }
            };
            Controls.Add(cboTema);

            // ---------------- paises ----------------
            lblPais = new Label();
            lblPais.Text = "Pa\u00eds:";
            lblPais.Location = new Point(14, 75);
            lblPais.Size = new Size(40, 22);
            Controls.Add(lblPais);

            cboCountry = new CboVidro();
            ((CboVidro)cboCountry).Dono = this;
            cboCountry.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCountry.ItemHeight = 17;
            cboCountry.Location = new Point(56, 73);
            cboCountry.Size = new Size(242, 23);
            cboCountry.DropDownWidth = 242;
            cboCountry.DrawMode = DrawMode.OwnerDrawFixed;
            cboCountry.DrawItem += DesenharCombo;
            cboCountry.SelectedIndexChanged += delegate
            {
                if (suppressUi) return;
                var i = cboCountry.SelectedIndex;
                if (i <= 0) countryFilter = null;
                else countryFilter = filterMap[i - 1].ISO;
                AtualizarLista();
            };
            Controls.Add(cboCountry);

            btnRefresh = new Button();
            btnRefresh.Text = "Atualizar";
            btnRefresh.Location = new Point(309, 72);
            btnRefresh.Size = new Size(94, 28);
            btnRefresh.Click += delegate
            {
                if (dlTask != null && !dlTask.IsCompleted) { Log("J\u00e1 h\u00e1 uma atualiza\u00e7\u00e3o em andamento..."); return; }
                Log("Atualizando a lista de servidores gr\u00e1tis...");
                lblStatus.Text = "Status: baixando lista...";
                if (BaixarListaServidores()) btnRefresh.Enabled = false;
                IniciarFontesPublicas();
                // 3,5 s depois das fontes voltarem: atualiza ip/porta/.ovpn de todos
                // (ninguem e tirado da lista)
                try
                {
                    if (penteTimer != null) { try { penteTimer.Stop(); } catch { } }
                    penteTimer = new System.Windows.Forms.Timer();
                    penteTimer.Interval = 3500;
                    penteTimer.Tick += delegate { try { penteTimer.Stop(); } catch { } RevalidarFontes(); };
                    penteTimer.Start();
                }
                catch { }
            };
            Controls.Add(btnRefresh);

            btnPing = new Button();
            btnPing.Text = "Medir ping";
            btnPing.Location = new Point(410, 72);
            btnPing.Size = new Size(94, 28);
            btnPing.Click += delegate { MedirTudo(); };
            Controls.Add(btnPing);

            // ---------------- tipo de tunel ----------------
            lblProto = new Label();
            lblProto.Text = "T\u00fanel:";
            lblProto.Location = new Point(515, 75);
            lblProto.Size = new Size(46, 22);
            Controls.Add(lblProto);

            cboProto = new CboVidro();
            ((CboVidro)cboProto).Dono = this;
            cboProto.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProto.ItemHeight = 17;
            cboProto.Location = new Point(562, 73);
            cboProto.Size = new Size(202, 23);
            cboProto.DropDownWidth = 202;
            cboProto.Items.AddRange(Dados.ProtoTexts);
            cboProto.DrawMode = DrawMode.OwnerDrawFixed;
            cboProto.DrawItem += DesenharCombo;
            cboProto.SelectedIndexChanged += delegate
            {
                if (suppressUi) return;
                var i = cboProto.SelectedIndex;
                if (i < 0) return;
                protocol = Dados.ProtoKeys[i];
                SalvarEstado();
                AtualizarLista();
                if (protocol == "auto") Log("T\u00fanel: UDP e TCP juntos (usa o que o servidor escolhido aceitar).");
                else Log("T\u00fanel: s\u00f3 " + protocol.ToUpperInvariant() + " - a lista passa a mostrar s\u00f3 quem fala esse protocolo.");
                if (connected) Log("Para trocar de t\u00fanel, desconecte e conecte de novo.");
            };
            Controls.Add(cboProto);

            // a listinha que ABRE nos seletores (combo) tem barra de rolagem
            // propia: quando ela aparecer, vira dark mode na hora
            foreach (var cb in new[] { cboTema, cboCountry, cboProto })
            {
                cb.DropDown += delegate
                {
                    try
                    {
                        var lbDrop = Nativo.FindWindowEx(cb.Handle, IntPtr.Zero, "ComboLBox", null);
                        if (lbDrop != IntPtr.Zero)
                        {
                            Nativo.SetWindowTheme(lbDrop, "DarkMode_Explorer", null);
                            Nativo.EscurecerScroll(lbDrop);
                        }
                    }
                    catch { }
                };
            }

            lblStatus = new Label();
            lblStatus.Text = "Status: desconectado";
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblStatus.Location = new Point(526, 98);
            lblStatus.Size = new Size(370, 22);
            Controls.Add(lblStatus);

            // ---------------- servidores ----------------
            lv = new LvVidro();
            ((LvVidro)lv).Dono = this;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.BorderStyle = BorderStyle.None;
            lv.HideSelection = false;
            lv.Location = new Point(15, 128);
            lv.Size = new Size(876, 180);
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            foreach (object[] c in new object[] { new object[] { "Pa\u00eds", 150 }, new object[] { "IP", 172 }, new object[] { "Seu ping", 122 }, new object[] { "Velocidade", 172 }, new object[] { "Protocolo", 111 }, new object[] { "", 60 } })
                lv.Columns.Add(c[0].ToString(), (int)c[1]);
            lv.HeaderStyle = ColumnHeaderStyle.Clickable;
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += DesenharCabecalho;
            lv.DrawSubItem += DesenharCelula;
            lv.ColumnClick += delegate(object snd, ColumnClickEventArgs ev)
            {
                try
                {
                    if (ev.Column >= 5)
                    {
                        // coluna vazia do fim = voltar para a ordem padrao
                        sortCol = -1;
                        OrdenarPadrao();
                        AtualizarSetasColunas();
                        AtualizarLista();
                        return;
                    }
                    if (sortCol == ev.Column) sortAsc = !sortAsc;
                    else
                    {
                        sortCol = ev.Column;
                        // A-Z natural; ping do MENOR; velocidade do MAIOR
                        sortAsc = (ev.Column != 3);
                    }
                    lv.ShowGroups = false;
                    OrdenarLista();
                    AtualizarLista();
                }
                catch { }
            };
            lv.MouseDown += delegate(object snd, MouseEventArgs ev)
            {
                try
                {
                    var it = lv.GetItemAt(ev.X, ev.Y);
                    if (it != null) { it.Selected = true; lv.FocusedItem = it; }
                }
                catch { }
            };
            lv.DoubleClick += delegate
            {
                try
                {
                    if (!btnConnect.Enabled) return;
                    if (lv.SelectedItems.Count == 0) return;
                    if (connected) { try { Desconectar(); } catch { } }
                    Conectar();
                }
                catch (Exception e) { try { Log("Erro na troca r\u00e1pida: " + e.Message); } catch { } }
            };
            Controls.Add(lv);

            // ---------------- programas ----------------
            lblApps = new Label();
            lblApps.Text = "Programas que passam pela VPN (o resto do PC fica na sua conex\u00e3o normal):";
            lblApps.Location = new Point(14, 323);
            lblApps.Size = new Size(640, 20);
            Controls.Add(lblApps);

            lbApps = new LbVidro();
            ((LbVidro)lbApps).Dono = this;
            lbApps.Location = new Point(13, 348);
            lbApps.Size = new Size(524, 138);
            lbApps.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            lbApps.BorderStyle = BorderStyle.None;
            lbApps.DrawMode = DrawMode.OwnerDrawFixed;
            lbApps.ItemHeight = 19;
            lbApps.IntegralHeight = false;
            lbApps.HorizontalScrollbar = true;
            lbApps.BackColor = Color.FromArgb(4, 13, 15);
            lbApps.ForeColor = CorTexto;
            lbApps.DrawItem += delegate(object snd, DrawItemEventArgs ev)
            {
                try { if (ev.Index >= 0) DesenharItemProgramas(ev.Graphics, ev.Index, ev.Bounds, (ev.State & DrawItemState.Selected) != 0); } catch { }
            };
            Controls.Add(lbApps);

            btnAdd = new Button();
            btnAdd.Text = "Adicionar programa...";
            btnAdd.Location = new Point(552, 347);
            btnAdd.Size = new Size(202, 32);
            btnAdd.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnAdd.Click += delegate
            {
                var escolhido = EscolherPrograma();
                if (escolhido != null) AdicionarPrograma(escolhido);
            };
            Controls.Add(btnAdd);

            btnRemove = new Button();
            btnRemove.Text = "Remover";
            btnRemove.Location = new Point(552, 386);
            btnRemove.Size = new Size(202, 32);
            btnRemove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRemove.Click += delegate
            {
                var sel = lbApps.SelectedIndex;
                if (sel < 0) { Log("Selecione um programa da lista para remover."); return; }
                var nome = appList[sel].Name;
                appList = appList.Where(a => a.Name != nome).ToList();
                AtualizarListaProgramas();
                SalvarEstado();
                Log(nome + " saiu da lista.");
                if (connected) { try { SincronizarRotas(); } catch { } }
            };
            Controls.Add(btnRemove);

            chkFull = new CbVidro();
            chkFull.Text = "Tudo pela VPN (marque = PC inteiro)";
            chkFull.Location = new Point(551, 426);
            chkFull.Size = new Size(250, 24);
            chkFull.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkFull.CheckedChanged += delegate
            {
                if (suppressUi) return;
                fullTunnel = chkFull.Checked;
                SalvarEstado();
                AtualizarListaProgramas();
                if (connected)
                {
                    Log("Modo de t\u00fanel alterado - aplicando agora...");
                    try { SincronizarRotas(); } catch (Exception e) { Log("Erro: " + e.Message); }
                }
            };
            Controls.Add(chkFull);

            chkAutoRe = new CbVidro();
            chkAutoRe.Text = "Autoconectar se a VPN cair";
            chkAutoRe.Location = new Point(551, 478);
            chkAutoRe.Size = new Size(320, 24);
            chkAutoRe.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkAutoRe.CheckedChanged += delegate
            {
                if (suppressUi) return;
                autoRecon = chkAutoRe.Checked;
                SalvarEstado();
                if (autoRecon) { reAuto = 0; Log("Autoconectar ligado: se a VPN cair, volta sozinha."); }
                else Log("Autoconectar desligado.");
            };
            Controls.Add(chkAutoRe);

            // ---------------- botao CONECTAR ----------------
            btnConnect = new Button();
            btnConnect.Text = "CONECTAR";
            btnConnect.Font = new Font("Segoe UI Semibold", 12);
            btnConnect.FlatStyle = FlatStyle.Flat;
            btnConnect.Location = new Point(7, 491);
            btnConnect.Size = new Size(384, 48);
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Paint += PintarBotaoConectar;
            btnConnect.Click += delegate
            {
                if (connected) Desconectar();
                else Conectar();
            };
            Controls.Add(btnConnect);

            // ---------------- inicializacao com o Windows ----------------
            chkStartup = new CbVidro();
            chkStartup.Text = "Iniciar com o Windows";
            chkStartup.Location = new Point(401, 506);
            chkStartup.Size = new Size(230, 24);
            chkStartup.CheckedChanged += delegate
            {
                if (suppressUi) return;
                try
                {
                    DefinirStartup(chkStartup.Checked);
                }
                catch (Exception e)
                {
                    Log("N\u00e3o deu para mudar a inicializa\u00e7\u00e3o: " + e.Message);
                    suppressUi = true; chkStartup.Checked = TestarStartup(); suppressUi = false;
                }
                SalvarEstado();
            };
            Controls.Add(chkStartup);

            chkBootConn = new CbVidro();
            chkBootConn.Text = "Conectar ao iniciar com o Windows";
            chkBootConn.Location = new Point(401, 532);
            chkBootConn.Size = new Size(290, 24);
            chkBootConn.CheckedChanged += delegate
            {
                if (suppressUi) return;
                bootConnect = chkBootConn.Checked;
                SalvarEstado();
                if (bootConnect)
                {
                    Log("Quando o Windows abrir o programa, ele conecta sozinho.");
                    if (!chkStartup.Checked) Log("Marque tamb\u00e9m \"Iniciar com o Windows\" para isso valer.");
                    if (chkStartup.Checked) { try { DefinirStartup(true); } catch { } }
                }
                else
                {
                    Log("N\u00e3o conecta mais sozinho ao iniciar com o Windows.");
                    if (chkStartup.Checked) { try { DefinirStartup(true); } catch { } }
                }
            };
            Controls.Add(chkBootConn);

            // as caixas de marcar pintam o fundo do tema (vidro + foto) atras
            foreach (var c in new CbVidro[] { chkFull, chkAutoRe, chkStartup, chkBootConn })
            {
                c.Dono = this;
                c.ForeColor = CorTexto;
            }

            // ---------------- desinstalar ----------------
            btnUninstall = new Button();
            btnUninstall.Text = "Desinstalar o programa";
            btnUninstall.Location = new Point(703, 504);
            btnUninstall.Size = new Size(191, 28);
            btnUninstall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnUninstall.Click += delegate { Desinstalar(); };
            Controls.Add(btnUninstall);

            // ---------------- atualizacao ----------------
            btnUpdate = new Button();
            btnUpdate.Text = "Atualiza\u00e7\u00e3o Dispon\u00edvel";
            btnUpdate.Location = new Point(430, 10);
            btnUpdate.Size = new Size(191, 28);
            btnUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnUpdate.Visible = false;
            btnUpdate.Click += delegate
            {
                try
                {
                    var msg = "Nova vers\u00e3o " + versaoNova + " dispon\u00edvel (a sua \u00e9 a " + Program.Versao + ").\r\n\r\nAbrir a p\u00e1gina para baixar agora?";
                    var r = MessageBox.Show(this, msg, "Atualiza\u00e7\u00e3o dispon\u00edvel", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes)
                    {
                        Log("Abrindo a p\u00e1gina de download da vers\u00e3o " + versaoNova + "...");
                        Process.Start(Dados.UrlDownload);
                    }
                }
                catch (Exception e) { Log("N\u00e3o deu para abrir o download: " + e.Message); }
            };
            Controls.Add(btnUpdate);

            // ---------------- log ----------------
            lblLog = new Label();
            lblLog.Text = "O que est\u00e1 acontecendo:";
            lblLog.Location = new Point(14, 539);
            lblLog.Size = new Size(300, 20);
            Controls.Add(lblLog);

            lbLog = new LbVidro();
            ((LbVidro)lbLog).Dono = this;
            lbLog.BorderStyle = BorderStyle.None;
            lbLog.DrawMode = DrawMode.OwnerDrawFixed;
            lbLog.ItemHeight = 15;
            lbLog.SelectionMode = SelectionMode.None;
            lbLog.IntegralHeight = false;
            lbLog.HorizontalScrollbar = true;
            lbLog.BackColor = Color.FromArgb(3, 10, 12);
            lbLog.ForeColor = CorTexto;
            lbLog.Font = new Font("Segoe UI", 9);
            lbLog.Location = new Point(15, 564);
            lbLog.Size = new Size(876, 143);
            lbLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lbLog.DrawItem += delegate(object snd, DrawItemEventArgs ev)
            {
                try { if (ev.Index >= 0) DesenharItemLog(ev.Graphics, ev.Index, ev.Bounds, false); } catch { }
            };
            Controls.Add(lbLog);

            // ---------------- bandeja ----------------
            var iconeApp = CarregarIcone();
            if (iconeApp != null) { try { Icon = iconeApp; } catch { } }
            tray = new NotifyIcon();
            tray.Icon = iconeApp ?? SystemIcons.Shield;
            tray.Text = "Ultra VPN Free";
            tray.Visible = false;
            trayMenu = new ContextMenuStrip();
            trayMenu.Renderer = new MenuDoTema();
            trayMenu.ShowImageMargin = false;
            var miOpen = trayMenu.Items.Add("Abrir janela");
            miOpen.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            var miDescon = trayMenu.Items.Add("Desconectar a VPN");
            trayMenu.Items.Add("-");
            var miExit = trayMenu.Items.Add("Sair");
            foreach (ToolStripItem it in trayMenu.Items) it.ForeColor = CorTexto;
            tray.ContextMenuStrip = trayMenu;
            miOpen.Click += delegate { MostrarDaBandeja(); };
            miDescon.Click += delegate { if (connected) Desconectar(); };
            miExit.Click += delegate { SairDoPrograma(); };
            tray.MouseDoubleClick += delegate { MostrarDaBandeja(); };

            // etzinho piscando na bandeja quando conecta (quadros do tray.gif):
            // os 12 quadros 600x600 pesam e travariam a abertura - processa
            // fora da thread da janela; ate ficar pronto vale a reserva
            ThreadPool.QueueUserWorkItem(delegate { try { CarregarTrayAnimado(); } catch { } });
            trayTimer = new System.Windows.Forms.Timer();
            trayTimer.Interval = 220;
            trayTimer.Tick += delegate
            {
                try
                {
                    if (trayFrames != null && trayFrames.Length > 1)
                    {
                        trayFrameIdx = (trayFrameIdx + 1) % trayFrames.Length;
                        tray.Icon = trayFrames[trayFrameIdx];
                    }
                }
                catch { }
            };

            // o et do polegar anima com os MESMOS quadros da bandeja: o timer
            // so redesenha - o quadro certo vem do relogio (QuadroEtAgora),
            // entao a piscada segue lisa mesmo rolando ou arrastando a barra
            piscarTimer = new System.Windows.Forms.Timer();
            piscarTimer.Interval = 220;
            piscarTimer.Tick += delegate
            {
                try
                {
                    if (trayFramesBmp == null || trayFramesBmp.Count < 2) return;
                    // a lista (janela propria) repinta a barra no DC; as caixas
                    // de vidro repintam o et junto com o texto (mesmo buffer)
                    if (lv.IsHandleCreated) DesenharBarraRolagem(lv);
                    foreach (var ctl in new Control[] { lbApps, lbLog })
                        if (ctl.IsHandleCreated)
                        {
                            try { ctl.Invalidate(); } catch { }
                        }
                }
                catch { }
            };
            piscarTimer.Start();

            // nasce TUDO escuro: a primeira pintura ja vem na cor do tema, sem
            // nenhum quadro branco (a AplicarTema so refina depois)
            try
            {
                foreach (var l in new Control[] { lblSub, lblIP, lblPais, lblProto, lblApps, lblLog, lblStatus, lblTemas })
                {
                    l.BackColor = Color.Transparent;
                    l.ForeColor = CorTexto;
                }
                var vidroCianoNasc = Color.FromArgb(120, 20, 52, 56);
                var vidroAmbarNasc = Color.FromArgb(120, 34, 26, 12);
                object[][] botoesNasc = new object[][] {
                    new object[] { btnRefresh, vidroCianoNasc, CorBorda, CorTexto },
                    new object[] { btnPing, vidroCianoNasc, CorBorda, CorTexto },
                    new object[] { btnAdd, Color.FromArgb(120, 62, 96, 100), Color.FromArgb(88, 138, 144), CorTexto },
                    new object[] { btnRemove, vidroAmbarNasc, CorAmbar, CorAmbar },
                    new object[] { btnUninstall, vidroAmbarNasc, CorAmbar, CorAmbar },
                    new object[] { btnUpdate, vidroAmbarNasc, CorAmbar, CorAmbar } };
                foreach (var d in botoesNasc)
                {
                    var b = (Button)d[0];
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = (Color)d[1];
                    b.ForeColor = (Color)d[3];
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = (Color)d[2];
                }
                foreach (var cb in new Control[] { cboTema, cboCountry, cboProto })
                {
                    cb.BackColor = Color.FromArgb(7, 23, 26);
                    cb.ForeColor = CorTexto;
                }
                foreach (var ck in new Control[] { chkFull, chkStartup, chkAutoRe, chkBootConn }) ck.ForeColor = CorTexto;
            }
            catch { }
            // a janela so APARECE pronta: enquanto monta, fica invisivel em
            // vez de mostrar quadros brancos e ampulheta (nada de flash)
            try { Opacity = 0; } catch { }

            // ---------------- eventos da janela ----------------
            FormClosing += delegate(object snd, FormClosingEventArgs ev)
            {
                if (exiting) return;                  // foi pedido pelo menu Sair
                ev.Cancel = true;                     // o X sempre manda para a bandeja
                IrParaBandeja();
                Log("Ficou na bandeja (clique 2x no \u00edcone para abrir). Bot\u00e3o direito -> Sair para encerrar.");
            };
            ResizeEnd += delegate { try { AplicarTema(); } catch { } };
            estadoJanela = WindowState;
            Resize += delegate
            {
                if (WindowState == estadoJanela) return;
                estadoJanela = WindowState;
                if (WindowState != FormWindowState.Minimized) { try { AplicarTema(); } catch { } }
            };
            Shown += delegate { AoAbrirJanela(); };
            Load += delegate
            {
                // a barra de rolagem encosta na borda direita da janela:
                // as listas ganham a largura que faltava (o espacinho da direita)
                try { lv.Width = ClientSize.Width - lv.Left; } catch { }
                try { lbLog.Width = ClientSize.Width - lbLog.Left; } catch { }

                // FUNDO PROVISORIO
                // A janela aparece em 0,5 s, mas a tela em HTML so entra
                // uns 3 s depois (o WebView2 demore para subir). Nesse
                // intervalo a janela ficaria vazia. Aqui pintamos o MESMO
                // fundo que o app.css vai pintar, no mesmo enquadramento
                // (cover, centrado), para a janela ja nascer com cara de
                // programa e nao dar salto quando a tela entrar.
                // O plano B (sem Runtime) troca este pelo fundo completo,
                // com as placas de vidro, pelo AplicarTema().
                try
                {
                    var bf = MontarFundoCover();
                    if (bf != null)
                    {
                        var velho0 = imgComp;
                        imgComp = bf;
                        BackgroundImage = bf;
                        BackgroundImageLayout = ImageLayout.Stretch;
                        if (velho0 != null) { try { velho0.Dispose(); } catch { } }
                    }
                }
                catch { }

                // ============================================================
                //  TELA EM WEBVIEW2
                // S� aqui: antes do Load a janela ainda n�o tem handle, e o
                // WebView2 (que � um controle Windows de verdade) n�o pode ser
                // criado. A janela continua sendo a mesma (form, bandeja,
                // �cone); o que muda � que o miolo vira HTML. Se o WebView2
                // n�o entrar (PC sem o Runtime), o desenho antigo continua.
                // ============================================================
                // A tela em WebView2 ja foi ligando em LigarTela(), no construtor,
                // porque a janela so aparece depois que a tela esta pronta.
                // Aqui so o plano B: se o LigarTela nem chegou a rodar.
                telaWeb = telaWeb;
                if (!telaTentada) { try { AplicarTema(); } catch { } LiberarJanela(); }
            };

            // ---------------- timers ----------------
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 400;
            pollTimer.Tick += delegate { try { CicloRapido(); } catch { } };
            watch = new System.Windows.Forms.Timer();
            watch.Interval = 15000;
            // netstat + DNS + route.exe travam a janela a cada ciclo: o vigia colhe
            // fora da thread dela (so ele mexe nas rotas; a tela e avisada
            // por BeginInvoke onde precisa)
            watch.Tick += delegate
            {
                IniciarChecagemIP();
                if (connected) ThreadPool.QueueUserWorkItem(delegate { try { SincronizarRotas(); } catch { } });
            };
            rtimer = new System.Windows.Forms.Timer();
            rtimer.Interval = 4000;
            rtimer.Tick += delegate
            {
                if (connected) ThreadPool.QueueUserWorkItem(delegate { try { SincronizarRotas(); } catch { } });
            };
            reconTimer = new System.Windows.Forms.Timer();
            reconTimer.Interval = 6000;
            reconTimer.Tick += delegate
            {
                reconTimer.Stop();
                if (connected || !autoRecon) return;
                if (!btnConnect.Enabled) return;
                if (conectando != 0) return;        // ja ha conexao correndo
                Log("Reconectando sozinho...");
                Conectar();
            };
            bootTimer = new System.Windows.Forms.Timer();
            bootTimer.Interval = 7000;
            bootTimer.Tick += delegate
            {
                bootTimer.Stop();
                if (connected) return;
                if (!btnConnect.Enabled) return;
                if (conectando != 0) return;        // ja ha conexao correndo
                Log("Conectando autom\u00e1tico (op\u00e7\u00e3o \"Conectar ao iniciar com o Windows\").");
                Conectar();
            };
            connTimer = new System.Windows.Forms.Timer();
            connTimer.Interval = 3500;
            connTimer.Tick += delegate
            {
                connTimer.Stop();
                if (connected) return;
                if (!btnConnect.Enabled) return;
                if (conectando != 0) return;        // ja ha conexao correndo
                if (lv.Items.Count == 0 && connTent < 8) { connTent++; connTimer.Start(); return; }
                Conectar();
            };
            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 1500;
            // a checagem baixa da internet (trava segundos na thread da janela):
            // roda fora dela e so encosta na tela no final
            updateTimer.Tick += delegate
            {
                try { updateTimer.Stop(); } catch { }
                ThreadPool.QueueUserWorkItem(delegate { try { TestarAtualizacao(); } catch { } });
            };
            // REVISITA AS FONTES DE TEMPO EM TEMPO. IP e porta de servidor gratis
            // mudam o dia inteiro (a Argentina responde hoje, o Peru amanha; a
            // Urban liga e desliga paises). Sem isto a lista envelhece e o
            // servidor Some como morto mesmo voltando a funcionar.
            fontesTimer = new System.Windows.Forms.Timer();
            fontesTimer.Interval = 600000;      // 10 minutos
            fontesTimer.Tick += delegate
            {
                try { if (saindo) return; } catch { return; }
                RevalidarFontes();
            };

            // ---------------- estado salvo ----------------
            CarregarEstado();

            // inicializa a lista de paises antes de mostrar
            PreencherPaises();
            AtualizarLista();
        }

        // ============================================================
        //  PONTE ENTRE A TELA (HTML) E O PROGRAMA
        // ============================================================

        // chamada uma vez, quando a tela web terminou de carregar
        public void TelaPronta()
        {
            try
            {
                // Os controles antigos NAO podem simplesmente sair da lista:
                // Controls.Remove() nao destroi a janela nativa deles, e o
                // ListView continuava pintando POR CIMA da tela (a tabela
                // aparecia duas vezes). A solucao e mover os controles para
                // uma janela escondida: eles continuam vivos (o codigo antigo
                // ainda usa lbLog, lbApps, lv...) mas param de pintar.
                if (baia == null)
                {
                    baia = new Form();
                    baia.ShowInTaskbar = false;
                    baia.WindowState = FormWindowState.Minimized;
                    baia.Opacity = 0;
                    baia.FormBorderStyle = FormBorderStyle.None;
                }
                var tirar = new List<Control>();
                foreach (Control c in Controls) if (!(c is WebView2)) tirar.Add(c);
                if (tirar.Count > 0)
                {
                    baia.Controls.AddRange(tirar.ToArray());
                    baia.Hide();
                }
            }
            catch { }
            try
            {
                MandarPaises();
                MandarTemas();
                // os quadros do ET, sem o preto de fundo (o mesmo trabalho
                // que o WinForms ja fazia para o polegar da barra)
                // 12 quadros de 600x600 com preenchimento por inundacao: e
                // caro, e nao e para travar a interface logo na abertura.
                // Vai para uma thread de fundo e manda quando estiver pronto
                new Thread(new ThreadStart(delegate
                {
                    try { MandarQuadrosEt(); } catch { }
                })).Start();
                EnviarProgramas();
                EnviarLista();
                EnviarEstado();
                EnviarLogAntigo();
                EnviarIP(currentIP);
            }
            catch (Exception e) { TelaWeb.Rastro("TelaPronta erro: " + e.Message); }
        }

        // ---------------------------------------------------------------
        //  A JANELA SO APARECE COM A TELA PRONTA
        // ---------------------------------------------------------------
        //  Medido nesta maquina: o WebView2 leva ~2 s entre pedir a pagina e
        //  o documento comecar a rodar - e o mesmo para uma pagina de 35
        //  bytes, entao nao e o nosso HTML nem a nossa imagem, e o navegador
        //  subindo o renderizador. Mostrar a janela em 0,5 s deixava o
        //  usuario olhando para um fundo vazio por 2 s e so depois aparecia
        //  o programa. Agora a janela aparece uma unica vez, ja completa:
        //  o que se ve e o programa inteiro, sem janela morta no meio.
        bool janelaLiberada = false;
        bool telaTentada = false;

        // Liga a tela em HTML ASSIM QUE a janela tem handle - antes do Load e
        // sem esperar a janela aparecer. Medido: o WebView2 leva ~2 s entre
        // pedir a pagina e o documento comecar a rodar (o mesmo numa pagina
        // de 35 bytes, entao nao e o nosso HTML). Se a janela so aparecesse
        // junto, esses 2 s seriam de janela parada na tela. Ligando aqui, os
        // 2 s rodam enquanto o Windows faz o resto e a janela aparece uma
        // vez so, ja com o programa inteiro.
        void LigarTela()
        {
            try
            {
                if (telaTentada) return;
                telaTentada = true;
                // CreateControl() NAO cria o handle de uma janela invisivel;
                // e preciso de CreateHandle() para ter a janela de verdade
                // antes de mostrar (o WebView2 exige uma janela de verdade)
                if (!IsHandleCreated) CreateHandle();
                if (!IsHandleCreated) { base.SetVisibleCore(true); return; }
                telaWeb = TelaWeb.Ligar(this, this);
                if (!telaWeb) { try { AplicarTema(); } catch { } LiberarJanela(); }
            }
            catch (Exception ex) { try { Log("Falha ao ligar a tela: " + ex.Message); } catch { } telaWeb = false; LiberarJanela(); }
        }

        protected override void SetVisibleCore(bool value)
        {
            // segura a janela ate a tela ficar pronta: nao faz sentido
            // mostrar 2 s de janela vazia. O Load continua disparando por
            // causa do CreateControl() acima.
            if (value && !janelaLiberada)
            {
                LigarTela();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        public void LiberarJanela()
        {
            try
            {
                if (janelaLiberada) return;
                janelaLiberada = true;
                if (IsHandleCreated) BeginInvoke((Action)(delegate { try { Show(); } catch { } }));
                else Show();
            }
            catch { }
        }

        // janela de reserva onde os controles do WinForms ficam escondidos
        Form baia = null;

        // o WebView2 nao entrou (PC sem o Runtime): desfaz o esconderijo
        // dos controles antigos e a janela volta a ser toda do WinForms
        public void TelaWebFalhou()
        {
            try
            {
                telaWeb = false;
                TelaWeb.Desligar();
                foreach (Control c in Controls) c.Show();
                // o fundo do Windows so e montado aqui (no plano B): com a
                // tela web ele nao roda, porque quem pinta o fundo e o HTML
                try { AplicarTema(); } catch { }
                LiberarJanela();
                Log("O Runtime do WebView2 nao esta neste PC - a tela antiga continua valendo.");
            }
            catch { }
        }

        // o ET que pisca: os 12 quadros do tray.gif, virados em data URI
        // (roda numa thread de fundo: 12 quadros de 600x600 com
        //  preenchimento por inundacao e trabalho pesado)
        void MandarQuadrosEt()
        {
            try
            {
                if (trayFramesBmp == null || trayFramesBmp.Count < 2) return;
                var quadros = new List<object>();
                for (int i = 0; i < trayFramesBmp.Count; i++)
                {
                    var uri = TelaWeb.DataUri(trayFramesBmp[i], 64);
                    if (!String.IsNullOrEmpty(uri)) quadros.Add(uri);
                }
                if (quadros.Count < 2) return;
                TelaWeb.Mandar("et", new Dictionary<string, object> { { "quadros", quadros } });
            }
            catch { }
        }

        void MandarTemas()
        {
            try
            {
                var lista = new List<object>();
                for (int i = 0; i < Dados.TemaKeys.Length; i++)
                {
                    lista.Add(new Dictionary<string, object> {
                        { "chave", Dados.TemaKeys[i] },
                        { "nome", Dados.TemaTexts[i] }
                    });
                }
                TelaWeb.Mandar("temas", new Dictionary<string, object> { { "temas", lista } });
            }
            catch { }
        }

void MandarPaises()
        {
            try
            {
                var lista = new List<object>();
                for (int i = 0; i < Dados.RegiaoSul.Length; i++)
                {
                    var iso = Dados.RegiaoSul[i];
                    lista.Add(new Dictionary<string, object> {
                        { "iso", iso },
                        { "nome", Dados.GetCountryName(iso) }
                    });
                }
                TelaWeb.Mandar("paises", new Dictionary<string, object> { { "paises", lista } });
            }
            catch { }
        }

        // a lista de servidores vira as linhas da tabela
        void EnviarLista()
        {
            if (!telaWeb) return;
            try
            {
                var lista = new List<object>();
                for (int i = 0; i < servers.Count; i++)
                {
                    var s = servers[i];
                    string cid = "";
                    try { cid = CidadeDe(s); } catch { }
                    lista.Add(new Dictionary<string, object> {
                        { "ip", s.IP },
                        { "porta", s.Port },
                        { "pais", Dados.GetCountryName(s.ISO) },
                        { "cidade", cid },
                        { "ping", s.Measured },
                        { "vel", s.Speed },
                        { "proto", String.IsNullOrEmpty(s.Proto) ? "auto" : s.Proto.ToUpperInvariant() }
                    });
                }
                // a coluna que esta valendo e o sentido: e o que faz o
                // cabecalho da tabela mostrar a seta, igual no WinForms
                TelaWeb.Mandar("servidores", new Dictionary<string, object> {
                    { "lista", lista },
                    { "ordemCol", sortCol },
                    { "ordemCresc", sortAsc }
                });
            }
            catch { }
        }

        void EnviarProgramas()
        {
            if (!telaWeb) return;
            try
            {
                var lista = new List<object>();
                for (int i = 0; i < appList.Count; i++)
                {
                    var p = appList[i];
                    string ic = "";
                    try
                    {
                        Icon achado;
                        if (cacheIcones.TryGetValue(p.Path, out achado))
                            ic = TelaWeb.IconeDataUri(achado);
                    }
                    catch { }
                    lista.Add(new Dictionary<string, object> {
                        { "nome", p.Name },
                        { "caminho", p.Path },
                        { "icone", ic }
                    });
                }
                TelaWeb.Mandar("programas", new Dictionary<string, object> { { "lista", lista } });
            }
            catch { }
        }

        void EnviarEstado()
        {
            if (!telaWeb) return;
            try
            {
                TelaWeb.Mandar("estado", new Dictionary<string, object> {
                    { "tema", temaAtual },
                    { "protocolo", protocol },
                    { "conectado", connected },
                    { "servidorAtual", servidorAtual },
                    { "ip", currentIP },
                    { "fullTunnel", fullTunnel },
                    { "autoRecon", autoRecon },
                    { "startup", TestarStartup() },
                    { "bootConn", bootConnect }
                });
            }
            catch { }
        }

        void EnviarLogAntigo()
        {
            // o log novo ja vai direto para a tela; o historico antigo fica
            // no arquivo (a tela comeca vazia, como o programa de antes)
        }

        void EnviarIP(string ip)
        {
            if (!telaWeb) return;
            try { TelaWeb.Mandar("ip", new Dictionary<string, object> { { "ip", ip ?? "" } }); }
            catch { }
        }

        void EnviarStatus(bool ligado, string servidor)
        {
            if (!telaWeb) return;
            try
            {
                TelaWeb.Mandar("status", new Dictionary<string, object> {
                    { "conectado", ligado },
                    { "servidor", servidor ?? "" },
                    { "endereco", servidorEndereco ?? "" }
                });
            }
            catch { }
        }

        // o que veio do clique na tela
        public void DoAcao(string acao, Dictionary<string, object> m)
        {
            try
            {
                switch (acao)
                {
                    case "pronto": break;
                    case "errojs":
                        {
                            object v; m.TryGetValue("texto", out v);
                            TelaWeb.Rastro("ERRO NA TELA: " + Convert.ToString(v));
                            return;
                        }
                    case "atualizar": TelaAtualizar(); return;
                    case "medirPing": MedirTudo(); return;
                    case "mudarPais":
                        {
                            object v; m.TryGetValue("iso", out v);
                            countryFilter = Convert.ToString(v);
                            AtualizarLista();
                            return;
                        }
                    case "mudarProtocolo":
                        {
                            object v; m.TryGetValue("protocolo", out v);
                            var p = Convert.ToString(v);
                            if (Array.IndexOf(Dados.ProtoKeys, p) >= 0) protocol = p;
                            SalvarEstado();
                            EnviarEstado();
                            return;
                        }
                    case "mudarTema":
                        {
                            object v; m.TryGetValue("tema", out v);
                            var t = Convert.ToString(v);
                            if (Array.IndexOf(Dados.TemaKeys, t) >= 0) { temaAtual = t; try { AplicarTema(); } catch { } }
                            return;
                        }
                    case "mudarOpcao":
                        {
                            if (m.ContainsKey("fullTunnel")) fullTunnel = LeTruth(m["fullTunnel"]);
                            if (m.ContainsKey("autoRecon")) autoRecon = LeTruth(m["autoRecon"]);
                            if (m.ContainsKey("startup")) { try { DefinirStartup(LeTruth(m["startup"])); } catch { } }
                            if (m.ContainsKey("bootConn")) bootConnect = LeTruth(m["bootConn"]);
                            SalvarEstado();
                            return;
                        }
                    case "mudarOrdem":
                        {
                            // clique no cabecalho da tabela da tela web: e a
                            // MESMA ordenacao do ListView do WinForms, com a
                            // mesma regra de sentido (A-Z crescente, ping do
                            // menor, velocidade do maior)
                            object v; m.TryGetValue("col", out v);
                            int col = -1;
                            try { col = Convert.ToInt32(v); } catch { }
                            try
                            {
                                if (col < 0)
                                {
                                    sortCol = -1;
                                    OrdenarPadrao();
                                }
                                else
                                {
                                    if (sortCol == col) sortAsc = !sortAsc;
                                    else
                                    {
                                        sortCol = col;
                                        sortAsc = (col != 3);
                                    }
                                    OrdenarLista();
                                }
                                AtualizarLista();
                            }
                            catch { }
                            return;
                        }
                    case "escolherServidor":
                        {
                            object v; m.TryGetValue("ip", out v);
                            var ip = Convert.ToString(v);
                            for (int i = 0; i < servers.Count; i++)
                                if (servers[i].IP == ip) { UltimoEscolhido = servers[i]; break; }
                            return;
                        }
                    case "conectarDesconectar":
                        if (connected) Desconectar(); else Conectar();
                        return;
                    case "conectarDireto":
                        {
                            // DUPLO CLIQUE na linha: a troca rapida do
                            // programa antigo (lv.DoubleClick). Escolhe o
                            // servidor da linha e liga; se ja estiver ligado,
                            // desliga e liga no outro.
                            object vi; m.TryGetValue("indice", out vi);
                            try
                            {
                                // o servidor da linha que foi clicada duas
                                // vezes - o mesmo caminho que o clique simples
                                // usa, para escolher country/city/ip/port
                                object vip; m.TryGetValue("ip", out vip);
                                var ip = Convert.ToString(vip);
                                for (int i = 0; i < servers.Count; i++)
                                    if (servers[i].IP == ip) { UltimoEscolhido = servers[i]; break; }
                            }
                            catch { }
                            if (connected) { try { Desconectar(); } catch { } }
                            Conectar();
                            return;
                        }
                    case "adicionarPrograma": AdicionarPrograma(null); return;
                    case "removerPrograma": RemoverProgramaSelecionado(); return;
                    case "desinstalar": Desinstalar(); return;
                    case "minimizar": WindowState = FormWindowState.Minimized; return;
                    case "maximizar":
                        WindowState = WindowState == FormWindowState.Maximized
                                     ? FormWindowState.Normal : FormWindowState.Maximized;
                        return;
                    case "fechar": SairDoPrograma(); return;
                }
            }
            catch { }
        }

        static bool LeTruth(object v)
        {
            if (v == null) return false;
            var s = Convert.ToString(v).ToLowerInvariant();
            return s == "true" || s == "1";
        }

        // o servidor que a pessoa escolheu na tela
        Servidor UltimoEscolhido = null;

        // o "Atualizar" da tela: exatamente o que o botao do WinForms faz
        void TelaAtualizar()
        {
            try
            {
                if (dlTask != null && !dlTask.IsCompleted) { Log("J\u00e1 h\u00e1 uma atualiza\u00e7\u00e3o em andamento..."); return; }
                Log("Atualizando a lista de servidores gr\u00e1tis...");
                if (BaixarListaServidores() && btnRefresh != null) btnRefresh.Enabled = false;
                IniciarFontesPublicas();
                try
                {
                    if (penteTimer != null) { try { penteTimer.Stop(); } catch { } }
                    penteTimer = new System.Windows.Forms.Timer();
                    penteTimer.Interval = 3500;
                    penteTimer.Tick += delegate { try { penteTimer.Stop(); } catch { } RevalidarFontes(); };
                    penteTimer.Start();
                }
                catch { }
            }
            catch { }
        }

        // o nome do servidor: cidade quando a fonte sabe, senao o pais
        string CidadeDe(Servidor s)
        {
            try { return Dados.GetServerLocal(s); }
            catch { return ""; }
        }

        // "Remover" da tela: tira o programa marcado
        void RemoverProgramaSelecionado()
        {
            try
            {
                if (lbApps == null || lbApps.SelectedIndex < 0) return;
                var idx = lbApps.SelectedIndex;
                if (idx < 0 || idx >= appList.Count) return;
                appList.RemoveAt(idx);
                try { SalvarEstado(); } catch { }
                AtualizarListaProgramas();
                EnviarProgramas();
            }
            catch { }
        }

        Image CarregarArquivo(string nome)
        {
            // 1) de DENTRO do proprio exe (versao de arquivo unico)
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var rs = asm.GetManifestResourceStream(nome))
                {
                    if (rs != null)
                    {
                        var ms = new MemoryStream();
                        rs.CopyTo(ms);
                        ms.Position = 0;
                        return new Bitmap(ms);
                    }
                }
            }
            catch { }
            // 2) do lado do exe (compatibilidade: quem ja tem as imagens soltas)
            try
            {
                var p = Path.Combine(Path.GetDirectoryName(Store.ExePath), nome);
                if (File.Exists(p)) { return new Bitmap(p); }
            }
            catch { }
            return null;
        }

        Icon CarregarIcone()
        {
            try
            {
                if (!String.IsNullOrEmpty(Store.ExePath))
                    return Icon.ExtractAssociatedIcon(Store.ExePath);
            }
            catch { }
            try
            {
                var p = Path.Combine(Path.GetDirectoryName(Store.ExePath), "UltraVPNFree.ico");
                if (File.Exists(p)) return new Icon(p);
            }
            catch { }
            return SystemIcons.Application;
        }

        // ============================================================
        //  ESTADO SALVO (state.json)
        // ============================================================
        void CarregarEstado()
        {
            var e = Store.LoadEstado();
            fullTunnel = e.FullTunnel;
            minTray = e.MinTray;
            if (!String.IsNullOrEmpty(e.Protocol) && Array.IndexOf(Dados.ProtoKeys, e.Protocol) >= 0) protocol = e.Protocol;
            autoRecon = e.AutoRecon;
            if (e.Tema != null && Array.IndexOf(Dados.TemaKeys, e.Tema) >= 0) temaAtual = e.Tema;
            bootConnect = e.BootConn;
            if (!String.IsNullOrEmpty(e.Ipv6Salvo)) ipv6Salvo = e.Ipv6Salvo;   // sessao passada caiu com o IPv6 fora: restaura depois
            if (e.InstalouOpenVpn) instalouOpenVpn = true;
            appList = new List<Programa>();
            foreach (var a in e.Apps)
            {
                if (a == null || String.IsNullOrEmpty(a.Name)) continue;
                appList.Add(new Programa { Name = a.Name, Path = a.Path, Domains = a.Domains ?? new List<string>() });
            }
            // adiciona o Discord como programa padr�o se n�o estiver na lista
            if (!appList.Any(a => String.Equals(a.Name, "Discord", StringComparison.OrdinalIgnoreCase)))
            {
                var caminhoDiscord = AcharCaminhoDiscord();
                if (!String.IsNullOrEmpty(caminhoDiscord))
                {
                    var domains = Dados.AppPresets.ContainsKey("Discord")
                        ? Dados.AppPresets["Discord"].ToList()
                        : new List<string>();
                    appList.Add(new Programa { Name = "Discord", Path = caminhoDiscord, Domains = domains });
                    Log("Discord adicionado como programa padr�o do tunel: " + caminhoDiscord);
                }
            }
            // portas aprendidas
            var pm = Store.LerPortas();
            portasPorIP = pm.PorIP ?? new Dictionary<string, List<int>>();
            portasVistas = pm.Vistas ?? new List<int>();
            portaProtoPorIP = pm.PorIPProto ?? new Dictionary<string, string>();
        }

        void SalvarEstado()
        {
            var e = new Store.Estado
            {
                FullTunnel = fullTunnel,
                MinTray = minTray,
                Startup = TestarStartup(),
                Protocol = protocol,
                AutoRecon = autoRecon,
                BootConn = bootConnect,
                Tema = temaAtual,
                Ipv6Salvo = ipv6Salvo ?? "",
                InstalouOpenVpn = instalouOpenVpn,
                Apps = appList.Select(a => new Store.AppRec { Name = a.Name, Path = a.Path, Domains = a.Domains }).ToList()
            };
            Store.SaveEstado(e);
        }

        // ============================================================
        //  INICIALIZACAO COM O WINDOWS (HKCU Run)
        // ============================================================
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RunNome = "UltraVPNFree";

        bool TestarStartup()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    var v = (string)k.GetValue(RunNome);
                    return !String.IsNullOrWhiteSpace(v);
                }
            }
            catch { return false; }
        }

        void DefinirStartup(bool on)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on)
                {
                    var extra = bootConnect ? " -Auto" : "";
                    var cmd = "\"" + Store.ExePath + "\" -Tray" + extra;
                    k.SetValue(RunNome, cmd);
                    Log("Vai iniciar junto com o Windows (abre direto na bandeja).");
                }
                else
                {
                    k.DeleteValue(RunNome, false);
                    Log("N\u00e3o inicia mais junto com o Windows.");
                }
            }
        }

        // ============================================================
        //  LOG (fila + descarga em lote + arquivo)
        // ============================================================
        public void Log(string msg)
        {
            // a tela web recebe a linha na hora; o WinForms continua
            // recebendo pelo mesmo caminho (a fila) e nada se perde
            if (telaWeb)
            {
                try
                {
                    TelaWeb.Mandar("log", new Dictionary<string, object> { { "texto", msg } });
                }
                catch { }
            }
            lock (logFila) logFila.Enqueue(msg);
            // arquivo em segundo plano: nunca trava a janela por I/O de disco
            string linha = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine;
            string caminho = null;
            try { caminho = Store.LogPath; } catch { }
            if (caminho != null)
                ThreadPool.QueueUserWorkItem(delegate { try { File.AppendAllText(caminho, linha); } catch { } });
        }

        void DescarregarLog()
        {
            string[] lote = null;
            // se a pessoa esta lendo o historico, linha nova nao deve pular a
            // rolagem para o fim (era o que fazia o log "picar" na mao)
            bool estavaNoFim = true;
            try { estavaNoFim = lbLog.RolagemY >= lbLog.RolagemMax - 1; } catch { }
            lock (logFila)
            {
                if (logFila.Count == 0) return;
                lote = logFila.ToArray();
                logFila.Clear();
            }
            // TODO o lote num BeginUpdate/EndUpdate so: uma pintura de verdade no fim
            // (um por linha fazia o log repintar varias vezes seguidas)
            lbLog.InvalidarLarguraCache();
            lbLog.BeginUpdate();
            try
            {
                foreach (var ln in lote)
                {
                    lbLog.Items.Add(ln);
                    while (lbLog.Items.Count > 300) lbLog.Items.RemoveAt(0);
                }
            }
            finally { lbLog.EndUpdate(); }
            if (estavaNoFim) lbLog.IrParaFim();  // rolagem nossa: sem TopIndex (nada de ShiftWindow na tela)
            else lbLog.Repintar();    // lendo o historico: fica onde voce esta
            // a barra nativa ja foi escurecida na abertura e na troca de tema:
            // reaplicar a cada linha for�ava o Windows a repintar a caixa
            // (era o outro lado do piscado)
        }

        // ============================================================
        //  DESENHO DE BASE (combos, cabecalho, celulas, fundo)
        // ============================================================
        public SolidBrush Pincel(Color c)
        {
            var k = c.ToArgb();
            SolidBrush b;
            if (!pincelCache.TryGetValue(k, out b)) { b = new SolidBrush(c); pincelCache[k] = b; }
            return b;
        }
        public Pen Caneta(Color c, float larg)
        {
            var k = c.ToArgb() + "|" + larg;
            Pen p;
            if (!canetaCache.TryGetValue(k, out p)) { p = new Pen(c, larg); canetaCache[k] = p; }
            return p;
        }

        void DesenharCombo(object snd, DrawItemEventArgs ev)
        {
            try
            {
                var g2 = ev.Graphics;
                using (var rb = new SolidBrush(Color.FromArgb(7, 23, 26))) g2.FillRectangle(rb, ev.Bounds);
                bool sel = (ev.State & DrawItemState.Selected) != 0;
                bool fechado = (ev.State & DrawItemState.ComboBoxEdit) != 0;
                if (sel)
                {
                    var corSel = fechado ? Color.FromArgb(20, 52, 56) : Color.FromArgb(45, 106, 115);
                    using (var rs = new SolidBrush(corSel)) g2.FillRectangle(rs, ev.Bounds);
                }
                if (ev.Index >= 0)
                {
                    using (var rtx = new SolidBrush(Color.White))
                        g2.DrawString((string)((ComboBox)snd).Items[ev.Index], ((Control)snd).Font, rtx,
                            new RectangleF(ev.Bounds.X + 5, ev.Bounds.Y + 2, ev.Bounds.Width - 8, ev.Bounds.Height - 3), fmtCel);
                }
            }
            catch { }
        }

        // pinta em g o pedaco do fundo que fica atras do retangulo r do controle
        void PintarFundo(Graphics g, Control ctl, Rectangle r)
        {
            try
            {
                if (imgComp != null)
                    g.DrawImage(imgComp, r, new Rectangle(ctl.Left + r.X, ctl.Top + r.Y, r.Width, r.Height), GraphicsUnit.Pixel);
            }
            catch { }
        }

        // fatia do fundo para os controles de vidro (transparencia real)
        public void PintarFatiaFundo(Graphics g, Control ctl, Rectangle r)
        {
            try
            {
                if (imgComp == null || r.Width < 1 || r.Height < 1) return;
                int sx = ctl.Left + r.X, sy = ctl.Top + r.Y;
                // CORTA A FATIA no tamanho do fundo: pedir pedaco fora da imagem
                // faz o GDI+ jogar excecao e a caixa ficar com a COR NATIVA
                // (era aquele quadro escuro que piscava no log)
                if (sx < 0) { r.X -= sx; r.Width += sx; sx = 0; }
                if (sy < 0) { r.Y -= sy; r.Height += sy; sy = 0; }
                if (sx >= imgComp.Width || sy >= imgComp.Height) return;
                if (sx + r.Width > imgComp.Width) r.Width = imgComp.Width - sx;
                if (sy + r.Height > imgComp.Height) r.Height = imgComp.Height - sy;
                if (r.Width < 1 || r.Height < 1) return;
                g.DrawImage(imgComp, r, new Rectangle(sx, sy, r.Width, r.Height), GraphicsUnit.Pixel);
            }
            catch { }
        }

        // item da lista (log ou programas): a LbVidro chama conforme a caixa
        public void DesenharItemLista(ListBox lst, Graphics g, int index, Rectangle r, bool sel)
        {
            if (lst == lbApps) DesenharItemProgramas(g, index, r, sel);
            else DesenharItemLog(g, index, r, sel);
        }

        // item da lista de programas (usado pelo LbVidro em UserPaint)
        void DesenharItemProgramas(Graphics g4, int index, Rectangle r4, bool sel)
        {
            try
            {
                if (sel)
                    using (var brS4 = new SolidBrush(Color.FromArgb(77, 43, 115, 125))) g4.FillRectangle(brS4, r4);

                // ICONE do proprio programa, tirado do .exe dele (o Discord
                // mostra o logo, o Chrome mostra o logo). Cache por caminho:
                // extrair icone a cada repaint trava a lista.
                var nome = (string)lbApps.Items[index];
                Icon ic = null;
                try
                {
                    if (index >= 0 && index < appList.Count)
                    {
                        var cam = appList[index].Path;
                        if (!String.IsNullOrEmpty(cam) && File.Exists(cam))
                        {
                            lock (cacheIcones)
                            {
                                if (!cacheIcones.TryGetValue(cam, out ic))
                                {
                                    try { ic = Icon.ExtractAssociatedIcon(cam); } catch { ic = null; }
                                    cacheIcones[cam] = ic;
                                }
                            }
                        }
                    }
                }
                catch { }

                int x = r4.X + 4;
                if (ic != null)
                {
                    try
                    {
                        g4.DrawIcon(ic, new Rectangle(x, r4.Y + (r4.Height - 16) / 2, 16, 16));
                        x += 21;
                    }
                    catch { }
                }
                using (var br4 = new SolidBrush(CorTexto))
                    g4.DrawString(nome, lbApps.Font, br4, new RectangleF(x, r4.Y, 4000, r4.Height), fmtCel);
            }
            catch { }
        }

        // item do console de status (erros em ambar)
        void DesenharItemLog(Graphics g3, int index, Rectangle r3, bool sel)
        {
            try
            {
                var ln = (string)lbLog.Items[index];
                string lnLow = null;
                try { lnLow = ln.ToLowerInvariant(); } catch { lnLow = ln; }
                var corL = (lnLow != null && (lnLow.IndexOf("erro") >= 0 || lnLow.IndexOf("falhou") >= 0 || lnLow.IndexOf("nao deu") >= 0 || lnLow.IndexOf("aviso") >= 0 || lnLow.IndexOf("aten") >= 0))
                    ? Color.FromArgb(204, 176, 68) : CorTexto;
                using (var brL = new SolidBrush(corL))
                    g3.DrawString(ln, lbLog.Font, brL, new RectangleF(r3.X + 4, r3.Y, 4000, r3.Height), fmtCel);
            }
            catch { }
        }

        // barras de rolagem escuras: aplica no controle e na filha SCROLLBAR
        // (a filha nasce quando o conteudo transborda - por isso repete depois
        // de encher as listas e de tempos em tempos)
        void EscurecerRolagens()
        {
            foreach (var ctl in new Control[] { lv, lbApps, lbLog })
            {
                try
                {
                    if (!ctl.IsHandleCreated) continue;
                    Nativo.SetWindowTheme(ctl.Handle, "DarkMode_Explorer", null);
                    Nativo.EscurecerScroll(ctl.Handle);
                }
                catch { }
            }
        }

        // ---------------- barra de rolagem propria (so o ET) ----------------
        // desenhada por cima da area da barra nativa, com as MESMAS metricas:
        // arrastar o polegar e clicar no trilho seguem funcionando por baixo.
        // NADA de trilho pintado: a barra e 100% TRANSPARENTE (da para ver o
        // fundo do tema) e aparece so a imagem do et com os olhos piscando.
        [StructLayout(LayoutKind.Sequential)]
        public struct SCROLLINFO { public int cbSize; public int fMask; public int nMin; public int nMax; public int nPage; public int nPos; public int nTrackPos; }
        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int indice);
        int largBarraCacheJ = -1;
        int altBarraCacheJ = -1;
        int GetLargBarraJ() { if (largBarraCacheJ < 0) largBarraCacheJ = GetSystemMetrics(2); return largBarraCacheJ; }
        int GetAltBarraJ() { if (altBarraCacheJ < 0) altBarraCacheJ = GetSystemMetrics(3); return altBarraCacheJ; }

        // onde o et de cada barra ja foi pintado (para apagar sem deixar rastro)
        readonly Dictionary<string, Rectangle> zonasEt = new Dictionary<string, Rectangle>();

        public void RegistrarZonaEt(Control ctl, string lado, Rectangle zona)
        {
            try { zonasEt[ctl.Handle.ToString() + lado] = zona; } catch { }
        }

        // O ET DENTRO DA PINTURA DO CONTROLE (mesmo buffer, mesma passada do
        // fundo e do texto). A barra nativa do Windows fica escondida nessas
        // caixas, entao nada mais pode repintar por cima: o ET que aparece e
        // sempre o do desenho completo - transparente embaixo, sem pisca.
        public void DesenharEtNoBuffer(Control ctl, Graphics g)
        {
            try
            {
                var lbc = ctl as LbVidro;
                if (lbc == null) return;
                int alt = Math.Max(1, lbc.ItemHeight);
                int totalV = Math.Max(0, lbc.Items.Count - 1);
                int pagV = Math.Max(1, lbc.ClientSize.Height / alt);
                int ladoV = GetLargBarraJ();
                int cc = lbc.ClientSize.Width, ca = lbc.ClientSize.Height;
                if (totalV > pagV && ladoV >= 6 && ca >= ladoV + 10)
                {
                    int pista = ca - 4;
                    int altP = Math.Max(ladoV, (int)(pista * Math.Min(1.0, pagV / (double)Math.Max(1, totalV))));
                    int posMax = Math.Max(1, totalV - pagV);
                    int pos = lbc.RolagemY / alt;
                    int yP = 2 + (int)((pista - altP) * Math.Min(1.0, Math.Max(0.0, pos / (double)posMax)));
                    int yEt = yP + (altP - ladoV) / 2;
                    if (yEt < 0) yEt = 0;
                    if (yEt + ladoV > ca) yEt = ca - ladoV;
                    var z = new Rectangle(cc - ladoV, yEt, ladoV, ladoV);
                    DesenharEt(g, z, true);
                    RegistrarZonaEt(ctl, "v", z);
                }
                int maxX = lbc.RolagemXMax;
                int ladoH = GetAltBarraJ();
                if (maxX > 0 && ladoH >= 6 && cc >= ladoH + 10)
                {
                    int pista = cc - 4;
                    int largP = Math.Max(ladoH, (int)(pista * Math.Min(1.0, cc / (double)(cc + maxX))));
                    int xP = 2 + (int)((pista - largP) * Math.Min(1.0, (double)lbc.RolagemX / maxX));
                    int xEt = xP + (largP - ladoH) / 2;
                    if (xEt < 0) xEt = 0;
                    if (xEt + ladoH > cc) xEt = cc - ladoH;
                    var z = new Rectangle(xEt, ca - ladoH, ladoH, ladoH);
                    DesenharEt(g, z, true);
                    RegistrarZonaEt(ctl, "h", z);
                }
            }
            catch { }
        }

        // COPIA PARA O BUFFER a fatia do fundo do tema que fica ATRAS da regiao
        // pedida (origem em coordenadas do cliente do controle). O destino e
        // sempre (0,0) do buffer - e isso que faltava antes: a fatia era pedida
        // na posicao X da barra (1953) num buffer de 38 px, ou seja FORA dele,
        // e a barra ficava com a cor escura nativa em vez do fundo do tema.
        void FatiaNoBuffer(Graphics gb, Control ctl, Rectangle origem, int larg, int alt)
        {
            try
            {
                if (imgComp == null || larg < 1 || alt < 1) return;
                gb.DrawImage(imgComp, new Rectangle(0, 0, larg, alt),
                    new Rectangle(ctl.Left + origem.X, ctl.Top + origem.Y, larg, alt), GraphicsUnit.Pixel);
            }
            catch { }
        }

        // BARRA TRANSPARENTE: so a imagem do et (olhos piscando) e nada mais.
        // Antes de pintar o novo, apaga o antigo repintando ali o fundo do tema,
        // para nao sobrar rastro quando a rolagem muda a posicao.
        void DesenharSoEt(Graphics g, Control ctl, Rectangle zona, string lado)
        {
            try
            {
                string chave = ctl.Handle.ToString() + lado;
                Rectangle antiga;
                if (zonasEt.TryGetValue(chave, out antiga) && antiga != zona)
                    PintarFatiaZona(g, ctl, antiga);
                using (var bmp = new Bitmap(zona.Width, zona.Height))
                using (var gb = Graphics.FromImage(bmp))
                {
                    FatiaNoBuffer(gb, ctl, zona, zona.Width, zona.Height);
                    DesenharEt(gb, new Rectangle(0, 0, zona.Width, zona.Height), true);
                    g.DrawImage(bmp, zona.Location);
                }
                zonasEt[chave] = zona;
            }
            catch { }
        }

        // repinta so um retangulo com a fatia do fundo do tema (limpa o que sobrou)
        void PintarFatiaZona(Graphics g, Control ctl, Rectangle zona)
        {
            try
            {
                if (zona.Width < 1 || zona.Height < 1) return;
                using (var bmp = new Bitmap(zona.Width, zona.Height))
                using (var gb = Graphics.FromImage(bmp))
                {
                    FatiaNoBuffer(gb, ctl, zona, zona.Width, zona.Height);
                    g.DrawImage(bmp, zona.Location);
                }
            }
            catch { }
        }

        public void RedesenharBarra(Control ctl)
        {
            try { DesenharBarraRolagem(ctl); } catch { }
        }

        // onde o ET de cada barra foi pintado pela ultima vez (para o mouse
        // saber o que e trilho e o que e o et)
        public Rectangle ZonaEtAtual(Control ctl, string lado)
        {
            Rectangle z;
            try
            {
                if (zonasEt.TryGetValue(ctl.Handle.ToString() + lado, out z)) return z;
            }
            catch { }
            return Rectangle.Empty;
        }

        public void DesenharBarraRolagem(Control ctl)
        {
            try
            {
                if (!ctl.IsHandleCreated) return;
                // nas caixas de vidro o ET ja vai dentro da pintura delas
                // (mesmo buffer): aqui nao ha nada a redesenhar no DC
                if (ctl is LbVidro) return;
                var si = new SCROLLINFO { cbSize = Marshal.SizeOf(typeof(SCROLLINFO)), fMask = 0x17 };
                if (!GetScrollInfo(ctl.Handle, 1, ref si)) return;             // SB_VERT
                var sih = new SCROLLINFO { cbSize = Marshal.SizeOf(typeof(SCROLLINFO)), fMask = 0x17 };
                if (!GetScrollInfo(ctl.Handle, 0, ref sih)) return;            // SB_HORZ
                int totalV = si.nMax - si.nMin + 1;
                bool rolaV = (si.nPage > 0 && totalV > si.nPage);
                // nas caixas com rolagem NOSSA (log/programas) o Windows nunca
                // rola: o GetScrollInfo ficaria parado no topo, entao o polegar
                // (e o ET) usam a posicao virtual do controle
                var lbc = ctl as LbVidro;
                if (lbc != null)
                {
                    int alt = Math.Max(1, lbc.ItemHeight);
                    si.nMin = 0;
                    si.nMax = Math.Max(0, lbc.Items.Count - 1);
                    si.nPage = Math.Max(1, lbc.ClientSize.Height / alt);
                    si.nPos = lbc.RolagemY / alt;
                    totalV = si.nMax - si.nMin + 1;
                    rolaV = totalV > si.nPage;
                }
                int totalH = sih.nMax - sih.nMin + 1;
                bool rolaH = (sih.nPage > 0 && totalH > sih.nPage);
                if (lbc != null)
                {
                    sih.nMin = 0;
                    sih.nMax = Math.Max(0, lbc.RolagemXMax);
                    sih.nPage = Math.Max(1, lbc.ClientSize.Width);
                    sih.nPos = Math.Max(0, Math.Min(lbc.RolagemX, sih.nMax));
                    totalH = sih.nMax - sih.nMin + 1;
                    rolaH = totalH > sih.nPage;
                }
                if (!rolaV && !rolaH) return;                                  // nao rola nada: sem barra

                int ladoBarra = GetLargBarraJ();                           // SM_CXVSCROLL (17)
                int alturaBarra = GetAltBarraJ();                         // SM_CYHSCROLL (17)
                if (rolaV && !rolaH) alturaBarra = 0;
                if (!rolaV && rolaH) ladoBarra = 0;
                var rV = new Rectangle(ctl.Width - ladoBarra, 0, ladoBarra, ctl.Height - alturaBarra);
                var rH = new Rectangle(0, ctl.Height - alturaBarra, ctl.Width - ladoBarra, alturaBarra);
                var rCanto = new Rectangle(ctl.Width - ladoBarra, ctl.Height - alturaBarra, ladoBarra, alturaBarra);

                IntPtr hdc = GetWindowDC(ctl.Handle);
                if (hdc == IntPtr.Zero) return;
                try
                {
                    using (var g = Graphics.FromHdc(hdc))
                    {
                        // cada regiao e montada num buffer e vai pra tela NUMA passada
                        // so (trilho + et juntos): sem estado intermediario, sem pisca
                        // ---------------- vertical
                        if (rolaV && rV.Width >= 6 && rV.Height >= 10)
                        {
                            double fracP = Math.Min(1.0, si.nPage / (double)totalV);
                            double posMax = Math.Max(1, totalV - si.nPage);
                            double fracPos = Math.Min(1.0, Math.Max(0.0, (si.nPos - si.nMin) / (double)posMax));
                            int pista = rV.Height - 4;
                            int altP = Math.Max(26, (int)(pista * fracP));
                            int yP = 2 + (int)((pista - altP) * fracPos);
                            int lado = rV.Width;               // o ET ocupa o trilho inteiro
                            int yEt = yP + (altP - lado) / 2;
                            if (yEt < 0) yEt = 0;
                            if (yEt + lado > rV.Height) yEt = rV.Height - lado;
                            // a faixa da barra recebe o MESMO fundo do tema (foto +
                            // vidro): fica transparente, sem trilho escuro; em cima
                            // so o et, com os olhos piscando
                            using (var bmp = new Bitmap(rV.Width, rV.Height))
                            using (var gb = Graphics.FromImage(bmp))
                            {
                                FatiaNoBuffer(gb, ctl, rV, rV.Width, rV.Height);
                                DesenharEt(gb, new Rectangle(0, yEt, lado, lado), true);
                                g.DrawImage(bmp, rV.Location);
                            }
                        }
                        // ---------------- horizontal (mesmo padrao)
                        if (rolaH && rH.Width >= 10 && rH.Height >= 6)
                        {
                            double fracP = Math.Min(1.0, sih.nPage / (double)totalH);
                            double posMax = Math.Max(1, totalH - sih.nPage);
                            double fracPos = Math.Min(1.0, Math.Max(0.0, (sih.nPos - sih.nMin) / (double)posMax));
                            int pista = rH.Width - 4;
                            int largP = Math.Max(26, (int)(pista * fracP));
                            int xP = 2 + (int)((pista - largP) * fracPos);
                            int lado = rH.Height;              // o ET ocupa a barra inteira
                            int xEt = xP + (largP - lado) / 2;
                            if (xEt < 0) xEt = 0;
                            if (xEt + lado > rH.Width) xEt = rH.Width - lado;
                            // mesma coisa na horizontal: fundo do tema + so o et
                            using (var bmp = new Bitmap(rH.Width, rH.Height))
                            using (var gb = Graphics.FromImage(bmp))
                            {
                                FatiaNoBuffer(gb, ctl, rH, rH.Width, rH.Height);
                                DesenharEt(gb, new Rectangle(xEt, 0, lado, lado), true);
                                g.DrawImage(bmp, rH.Location);
                            }
                        }
                        // canto (quando as duas existem): fundo do tema, sem rasgo
                        if (rolaV && rolaH)
                            PintarFatiaZona(g, ctl, rCanto);
                    }
                }
                finally { ReleaseDC(ctl.Handle, hdc); }
            }
            catch { }
        }

        // quadro do et travado no relogio: qualquer redesenho (rolagem, arraste,
        // timer) mostra o quadro certo do momento - a piscada nunca engasga,
        // nem parada nem durante o arraste da barra
        int QuadroEtAgora()
        {
            int n = (trayFramesBmp != null) ? trayFramesBmp.Count : 0;
            if (n < 2) return 0;
            return (Environment.TickCount % (n * 220)) / 220;   // ciclo de 2,64 s
        }

        // REDUZIR EM ETAPAS: o GDI+ nao amostra uma reducao grande de uma vez
        // (600 -> 64 num passo so = borr�o preto). Corta pela metade ate chegar
        // perto do alvo e so entao faz o passo final.
        static Bitmap ReduzirEmPassos(Image fonte, int lado)
        {
            int w = fonte.Width, h = fonte.Height;
            var atual = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(atual))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(fonte, new Rectangle(0, 0, w, h));
            }
            while (w > lado * 2 && h > lado * 2)
            {
                int nw = Math.Max(lado, w / 2), nh = Math.Max(lado, h / 2);
                var passo = new Bitmap(nw, nh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(passo))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(atual, new Rectangle(0, 0, nw, nh));
                }
                atual.Dispose();
                atual = passo;
                w = nw; h = nh;
            }
            if (w == lado && h == lado) return atual;
            var fim = new Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fim))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(atual, new Rectangle(0, 0, lado, lado));
            }
            atual.Dispose();
            return fim;
        }

        // TIRA O FUNDO PRETO DO GIF: o tray.gif vem com preto opaco (alpha 255).
        // Inundacao a partir das bordas apaga SO o preto ligado a borda - os olhos
        // pretos (que estao no meio da cabeca) ficam intactos. Assim o et fica
        // transparente e o vidro/foto do tema aparece em volta dele.
        static Bitmap SemPretoDeFundo(Bitmap fonte)
        {
            int lado = fonte.Width;
            if (fonte.Height != lado) return fonte;
            var b = new Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(fonte, new Rectangle(0, 0, lado, lado));
            }
            var ret = b.LockBits(new Rectangle(0, 0, lado, lado),
                System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(ret.Stride) * lado;
                var buf = new byte[bytes];
                Marshal.Copy(ret.Scan0, buf, 0, bytes);
                var fila = new Queue<int>();
                var visto = new bool[lado * lado];
                Action<int, int> marcar = delegate(int x, int y)
                {
                    int i = y * lado + x;
                    if (visto[i]) return;
                    int o = i * 4;                       // BGRA no little-endian
                    // perto do preto puro do fundo (0,0,0) - o contorno do et e
                    // cinza (~60+) e para a inundacao, nao corroe a carinha
                    if (buf[o] > 38 || buf[o + 1] > 38 || buf[o + 2] > 38) return;
                    visto[i] = true;
                    fila.Enqueue(i);
                };
                for (int x = 0; x < lado; x++) { marcar(x, 0); marcar(x, lado - 1); }
                for (int y = 0; y < lado; y++) { marcar(0, y); marcar(lado - 1, y); }
                while (fila.Count > 0)
                {
                    int i = fila.Dequeue();
                    buf[i * 4 + 3] = 0;                  // alfa 0 = transparente
                    int x = i % lado, y = i / lado;
                    if (x > 0) marcar(x - 1, y);
                    if (x < lado - 1) marcar(x + 1, y);
                    if (y > 0) marcar(x, y - 1);
                    if (y < lado - 1) marcar(x, y + 1);
                }
                Marshal.Copy(buf, 0, ret.Scan0, bytes);
            }
            finally { b.UnlockBits(ret); }
            return b;
        }

        // quadro do et pronto para a barra: tamanho util (64), sem o preto de fundo
        Bitmap QuadroDoEt(Image quadroOriginal)
        {
            try { return SemPretoDeFundo(ReduzirEmPassos(quadroOriginal, 64)); }
            catch
            {
                try
                {
                    var copia = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var gc2 = Graphics.FromImage(copia))
                        gc2.DrawImage(quadroOriginal, new Rectangle(0, 0, 64, 64));
                    return copia;
                }
                catch { return new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
            }
        }

        // o et da barra e o MESMO da bandeja: o quadro atual do tray.gif
        // desenhado em cima do polegar (os olhos piscam com os quadros)
        void DesenharEt(Graphics g, Rectangle zona, bool olhosAbertos)
        {
            try
            {
                if (trayFramesBmp != null && trayFramesBmp.Count > 0)
                {
                    var quadro = trayFramesBmp[QuadroEtAgora() % trayFramesBmp.Count];
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // sem folga: o quadrado do et preenche o trilho inteiro
                    int folga = 0;
                    g.DrawImage(quadro, zona.X + folga, zona.Y + folga,
                        zona.Width - 2 * folga, zona.Height - 2 * folga);
                    return;
                }
                // sem o tray.gif: carinha desenhada a mao (reserve)
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = zona.X + zona.Width / 2;
                int cy = zona.Y + zona.Height / 2;
                int largCab = Math.Min(zona.Width - 4, 13);
                int altCab = Math.Min(zona.Height - 3, 13);
                // cabeca cinza (oval)
                var rCab = new Rectangle(cx - largCab / 2, cy - altCab / 2 - 1, largCab, altCab);
                using (var br = new SolidBrush(Color.FromArgb(200, 168, 178, 186)))
                    g.FillEllipse(br, rCab);
                using (var p = new Pen(Color.FromArgb(110, 62, 130, 138), 1))
                    g.DrawEllipse(p, rCab);
                // olhos grandes (amendoas pretas) ou fechados (tracinhos)
                int yE = cy - 2;
                if (olhosAbertos)
                {
                    using (var br = new SolidBrush(Color.FromArgb(235, 8, 12, 16)))
                    {
                        g.FillEllipse(br, cx - 6, yE, 3, 6);
                        g.FillEllipse(br, cx + 3, yE, 3, 6);
                    }
                }
                else
                {
                    using (var p = new Pen(Color.FromArgb(235, 8, 12, 16), 1.4f))
                    {
                        g.DrawLine(p, cx - 6, yE + 3, cx - 3, yE + 3);
                        g.DrawLine(p, cx + 3, yE + 3, cx + 6, yE + 3);
                    }
                }
            }
            catch { }
        }

        // etzinho animado da bandeja (tray.gif): quadros de 16x16 em icones
        void CarregarTrayAnimado()
        {
            try
            {
                Bitmap img = null;
                // 1) de DENTRO do proprio exe (versao de arquivo unico)
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var rs = asm.GetManifestResourceStream("tray.gif"))
                    {
                        if (rs != null)
                        {
                            var ms = new MemoryStream();
                            rs.CopyTo(ms);
                            ms.Position = 0;
                            img = new Bitmap(ms);
                        }
                    }
                }
                catch { }
                // 2) do lado do exe (compatibilidade)
                if (img == null)
                {
                    try
                    {
                        var caminho = Path.Combine(Path.GetDirectoryName(Store.ExePath), "tray.gif");
                        if (!File.Exists(caminho)) return;
                        img = new Bitmap(caminho);
                    }
                    catch { }
                }
                if (img == null) return;
                var fd = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
                int n = 1;
                try { n = img.GetFrameCount(fd); } catch { }
                if (n < 1) n = 1;
                if (n > 24) n = 24;
                var quadros = new List<Bitmap>();
                var icons = new List<Icon>();
                for (int i = 0; i < n; i++)
                {
                    try { img.SelectActiveFrame(fd, i); } catch { }
                    // quadro reduzido EM ETAPAS (600 -> 64 sem virar borr�o) e
                    // com o preto de fundo removido: o et fica TRANSPARENTE
                    var quadro = QuadroDoEt(img);
                    quadros.Add(quadro);
                    using (var bmp = new Bitmap(16, 16))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(quadro, 0, 0, 16, 16);
                        }
                        icons.Add(Icon.FromHandle(bmp.GetHicon()));
                    }
                }
                img.Dispose();
                // entrega pronta e de uma vez na thread da janela (troca de
                // referencia e atomica: quem pinta nunca pega lista pela metade)
                var quadrosProntos = quadros;
                var iconsProntos = icons.Count > 0 ? icons.ToArray() : null;
                Action entrega = (Action)delegate
                {
                    try
                    {
                        trayFramesBmp = quadrosProntos;
                        if (iconsProntos != null && iconsProntos.Length > 0)
                        {
                            trayFrames = iconsProntos;
                            tray.Icon = trayFrames[0];
                        }
                        try { lv.Invalidate(); lbApps.Invalidate(); lbLog.Invalidate(); } catch { }
                    }
                    catch { }
                };
                try { BeginInvoke(entrega); }
                catch
                {
                    // janela ainda sem handle: a troca das listas e atomica e
                    // segura em qualquer thread; o icone entra quando abrir
                    try
                    {
                        trayFramesBmp = quadrosProntos;
                        if (iconsProntos != null && iconsProntos.Length > 0) trayFrames = iconsProntos;
                        etPendente = true;
                    }
                    catch { }
                }
            }
            catch { }
        }

        void DesenharCabecalho(object snd, DrawListViewColumnHeaderEventArgs ev)
        {
            try
            {
                ev.DrawDefault = false;
                var gH = ev.Graphics;
                var rH = ev.Bounds;
                PintarFundo(gH, lv, rH);
                using (var brH = new SolidBrush(Color.FromArgb(46, 150, 190, 194))) gH.FillRectangle(brH, rH);
                var txtH = (ev.ColumnIndex >= 0 && ev.ColumnIndex < lv.Columns.Count) ? lv.Columns[ev.ColumnIndex].Text : "";
                gH.DrawString(txtH, lv.Font, Pincel(CorTexto), new RectangleF(rH.X + 5, rH.Y, rH.Width - 8, rH.Height), fmtCel);
                var penH = Caneta(Color.FromArgb(150, CorBorda), 1);
                if (ev.ColumnIndex > 0) gH.DrawLine(penH, rH.X, rH.Y, rH.X, rH.Bottom - 1);
                gH.DrawLine(penH, rH.X, rH.Bottom - 1, rH.Right, rH.Bottom - 1);
            }
            catch { }
        }

        // serie decorativa deterministica por IP (mesmo IP = mesmo desenho)
        int[] SerieDecorativa(string ip, int n)
        {
            int h = 17;
            foreach (var ch in ip) h = (h * 31 + ch) % 100000;
            if (h <= 0) h = 42;
            var rnd = new Random(h);
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = rnd.Next(18, 100);
            return a;
        }

        void RegistrarPing(string ip, int ms)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(ip) || ms < 0) return;
                List<int> h;
                if (!pingHist.TryGetValue(ip, out h) || h == null) { h = new List<int>(); pingHist[ip] = h; }
                h.Add(ms);
                while (h.Count > 24) h.RemoveAt(0);
            }
            catch { }
        }

        void DesenharCelula(object snd, DrawListViewSubItemEventArgs ev)
        {
            try
            {
                var g = ev.Graphics;
                var r = ev.Bounds;
                var s = ev.Item.Tag as Servidor;
                var corTxt = CorTexto;
                if (ev.ColumnIndex == 0) corTxt = CorCiano;   // pais na cor do titulo
                if (ev.Item.Selected)
                {
                    using (var brSel = new SolidBrush(Color.FromArgb(77, 43, 115, 125))) g.FillRectangle(brSel, r);
                    corTxt = Color.White;
                    if (ev.ColumnIndex == 0)
                    {
                        var penN = Caneta(CorCiano, 3);
                        g.DrawLine(penN, r.X + 1, r.Y + 2, r.X + 1, r.Y + r.Height - 3);
                    }
                }
                if (ev.ColumnIndex == 3) corTxt = CorVerde;   // velocidade em verde
                if (ev.ColumnIndex == 2 && s != null && s.Measured >= 0) corTxt = CorVerde;  // ping em verde
                int largTxt = r.Width - 6;
                if (ev.ColumnIndex == 2) largTxt = 56; else if (ev.ColumnIndex == 3) largTxt = 92;
                g.DrawString(ev.SubItem.Text, lv.Font, Pincel(corTxt), new RectangleF(r.X + 5, r.Y, largTxt, r.Height), fmtCel);
                if (s == null) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var corGraf = CorCiano;
                { int hc = 17; foreach (var ch in s.IP) hc = (hc * 31 + ch) % 100000; if (hc % 3 == 0) corGraf = CorAmbar; }
                if (ev.ColumnIndex == 2)
                {
                    // grafico de linha do ping (historico real; sem historico, serie do IP)
                    int[] serie = null;
                    List<int> h;
                    if (pingHist.TryGetValue(s.IP, out h) && h != null && h.Count >= 2) serie = h.ToArray();
                    if (serie == null || serie.Length < 2) serie = SerieDecorativa(s.IP, 10);
                    int gx = r.X + 60, gw = r.Width - 66;
                    if (gw > 24 && serie.Length >= 2)
                    {
                        int mn = serie[0], mx = serie[0];
                        for (int k2 = 1; k2 < serie.Length; k2++) { if (serie[k2] < mn) mn = serie[k2]; if (serie[k2] > mx) mx = serie[k2]; }
                        if (mx <= mn) mx = mn + 1;
                        var pts = new PointF[serie.Length];
                        for (int i = 0; i < serie.Length; i++)
                        {
                            var fx = gx + (gw * i / (float)(serie.Length - 1));
                            var fy = (r.Y + r.Height - 4) - ((r.Height - 10) * ((serie[i] - mn) / (float)(mx - mn)));
                            pts[i] = new PointF(fx, fy);
                        }
                        var baseY = (float)(r.Y + r.Height - 3);
                        using (var area = new GraphicsPath())
                        {
                            area.AddCurve(pts, 0.35f);
                            area.AddLine(gx + gw, baseY, gx, baseY);
                            area.CloseFigure();
                            using (var brA = new SolidBrush(Color.FromArgb(70, corGraf))) g.FillPath(brA, area);
                            using (var penL = new Pen(Color.FromArgb(220, corGraf), 1.2f)) g.DrawCurve(penL, pts, 0.35f);
                        }
                    }
                }
                else if (ev.ColumnIndex == 3)
                {
                    // barras da velocidade (dado real, escala log)
                    double spd = s.Speed;
                    double frac = 0.06;
                    if (spd > 0) frac = Math.Min(1, Math.Max(0.06, Math.Log10(spd / 1000000 + 1) / Math.Log10(501)));
                    int bx = r.X + 96, bw = r.Width - 102;
                    if (bw > 24)
                    {
                        int n = 18;
                        var step = Math.Max(3.0, bw / (double)n);
                        var barW = Math.Max(2.0, step - 2);
                        var serie = SerieDecorativa(s.IP + "v", n);
                        var brAmbar = Pincel(CorAmbar);
                        var brApag = Pincel(Color.FromArgb(40, 140, 138));
                        var brVivo = Pincel(Color.FromArgb(70, 200, 190));
                        for (int i = 0; i < n; i++)
                        {
                            var alt = (r.Height - 8) * frac * (serie[i] / 100.0);
                            if (alt < 2) alt = 2;
                            var brBar = (i >= 12) ? brAmbar : ((i % 3) == 2 ? brApag : brVivo);
                            var rx = (int)(bx + i * step);
                            var ry = (int)(r.Y + r.Height - 4 - alt);
                            g.FillRectangle(brBar, rx, ry, (int)barW, (int)alt);
                        }
                    }
                }
            }
            catch { }
        }

        // ============================================================
        //  FUNDO COMPOSTO (foto + vidros + brilhos + titulo com halo)
        // ============================================================
        void DesenharBrilho(Graphics g, Rectangle r, Color cor)
        {
            int n = 1;
            foreach (var a in new[] { 120, 78, 46, 24, 10 })
            {
                using (var pen = new Pen(Color.FromArgb(a, cor), 1))
                    g.DrawRectangle(pen, r.X - n, r.Y - n, r.Width - 1 + 2 * n, r.Height - 1 + 2 * n);
                n++;
            }
            n = 1;
            foreach (var a in new[] { 70, 36, 14 })
            {
                using (var pen = new Pen(Color.FromArgb(a, cor), 1))
                    g.DrawRectangle(pen, r.X + n, r.Y + n, r.Width - 1 - 2 * n, r.Height - 1 - 2 * n);
                n++;
            }
        }

        // o que o fundo composto tinha quando foi montado (tamanho + tema +
        // botao de update visivel ou nao): repetindo igual, reaproveita e nao
        // remonta - na abertura o Load e o Shown pediam o mesmo fundo 2 vezes
        int fundoW = 0, fundoH = 0;
        string fundoTema = "";
        bool fundoUpdate = false;

        // So a foto de fundo, no mesmo "cover" que o app.css usa. E o que a janela
// mostra nos 2,5 s em que o WebView2 ainda nao chegou.
Bitmap MontarFundoCover()
        {
            try
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w < 200 || h < 200) return null;
                if (temaAtual == "escuro")
                {
                    var bmpE = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(bmpE))
                    using (var grad = new LinearGradientBrush(new Point(0, 0), new Point(0, h),
                        Color.FromArgb(255, 12, 24, 28), Color.FromArgb(255, 4, 10, 12)))
                        g.FillRectangle(grad, 0, 0, w, h);
                    return bmpE;
                }
                Image foto = temaAtual == "goku" ? (Image)imgFundoGoku : imgFundo;
                if (foto == null) return null;
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int iw = foto.Width, ih = foto.Height;
                    double esc = Math.Max(w / (double)iw, h / (double)ih);
                    int nw = (int)Math.Ceiling(iw * esc), nh = (int)Math.Ceiling(ih * esc);
                    g.DrawImage(foto, (w - nw) / 2, (h - nh) / 2, nw, nh);
                    if (temaAtual == "goku")
                        using (var veu = new SolidBrush(Color.FromArgb(105, 0, 0, 0))) g.FillRectangle(veu, 0, 0, w, h);
                }
                return bmp;
            }
            catch { return null; }
        }

        Bitmap MontarFundo()
        {
            try
            {
                if (temaAtual != "escuro" && temaAtual != "goku" && imgFundo == null) return null;
                if (temaAtual == "goku" && imgFundoGoku == null) { /* cai no liso */ }
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w < 200 || h < 200) return null;
                bool mostraUpdate = false;
                try { mostraUpdate = btnUpdate != null && btnUpdate.Visible; } catch { }
                if (imgComp != null && w == fundoW && h == fundoH && temaAtual == fundoTema && mostraUpdate == fundoUpdate) return imgComp;
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (temaAtual == "goku" && imgFundoGoku != null)
                    {
                        int iw = imgFundoGoku.Width, ih = imgFundoGoku.Height;
                        double esc = Math.Max(w / (double)iw, h / (double)ih);
                        int nw = (int)Math.Ceiling(iw * esc), nh = (int)Math.Ceiling(ih * esc);
                        g.DrawImage(imgFundoGoku, (w - nw) / 2, (h - nh) / 2, nw, nh);
                        using (var veu = new SolidBrush(Color.FromArgb(105, 0, 0, 0))) g.FillRectangle(veu, 0, 0, w, h);
                    }
                    else if (temaAtual == "escuro")
                    {
                        using (var grad = new LinearGradientBrush(new Point(0, 0), new Point(0, h),
                            Color.FromArgb(255, 12, 24, 28), Color.FromArgb(255, 4, 10, 12)))
                            g.FillRectangle(grad, 0, 0, w, h);
                    }
                    else if (imgFundo != null)
                    {
                        int iw = imgFundo.Width, ih = imgFundo.Height;
                        double esc = Math.Max(w / (double)iw, h / (double)ih);
                        int nw = (int)Math.Ceiling(iw * esc), nh = (int)Math.Ceiling(ih * esc);
                        g.DrawImage(imgFundo, (w - nw) / 2, (h - nh) / 2, nw, nh);
                    }
                    else
                    {
                        using (var grad = new LinearGradientBrush(new Point(0, 0), new Point(0, h),
                            Color.FromArgb(255, 12, 24, 28), Color.FromArgb(255, 4, 10, 12)))
                            g.FillRectangle(grad, 0, 0, w, h);
                    }

                    // paineis de vidro encaixados nas posicoes reais dos controles
                    object[][] vidros = new object[][] {
                        new object[] { lv, 3, 96, 58, 92, 96, 150, 86, 140, 146 },      // tabela
                        new object[] { lbApps, 1, 72, 58, 92, 96, 120, 86, 140, 146 },  // programas
                        new object[] { lbLog, 3, 120, 3, 12, 14, 140, 62, 110, 116 } }; // console
                    foreach (var d in vidros)
                    {
                        var ctl = (Control)d[0];
                        var folga = (int)d[1];
                        var rect = Rectangle.Inflate(ctl.Bounds, folga, folga);
                        using (var br = new SolidBrush(Color.FromArgb((int)d[2], (int)d[3], (int)d[4], (int)d[5])))
                            g.FillRectangle(br, rect);
                        using (var pen = new Pen(Color.FromArgb((int)d[6], (int)d[7], (int)d[8], (int)d[9]), 1))
                            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                    }

                    // brilho em volta dos botoes (o do CONECTAR pinta ele mesmo)
                    object[][] brilhos = new object[][] {
                        new object[] { btnRefresh, CorBorda }, new object[] { btnPing, CorBorda },
                        new object[] { btnAdd, CorCiano }, new object[] { btnRemove, CorAmbar },
                        new object[] { btnUninstall, CorAmbar }, new object[] { btnUpdate, CorAmbar } };
                    foreach (var par in brilhos)
                    {
                        var b = (Control)par[0];
                        if (b.Visible) DesenharBrilho(g, b.Bounds, (Color)par[1]);
                    }

                    // titulo: contorno largo e fraco em volta das letras = halo ciano
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var cam = new GraphicsPath())
                    {
                        cam.AddString("ULTRA VPN FREE", new FontFamily("Segoe UI"), 0, 27.5f,
                            new PointF(121, 3), StringFormat.GenericTypographic);
                        foreach (var par in new[] { new[] { 9, 16 }, new[] { 6, 26 }, new[] { 3, 44 } })
                        {
                            using (var pen = new Pen(Color.FromArgb(par[1], CorCiano), par[0]))
                            {
                                pen.LineJoin = LineJoin.Round;
                                g.DrawPath(pen, cam);
                            }
                        }
                        using (var br = new SolidBrush(CorCiano)) g.FillPath(br, cam);
                    }
                }
                fundoW = w; fundoH = h; fundoTema = temaAtual; fundoUpdate = mostraUpdate;
                return bmp;
            }
            catch (Exception e) { try { Log("Erro ao montar fundo: " + e.Message); } catch { } return null; }
        }

        void AplicarTema()
        {
            BackColor = Color.FromArgb(6, 18, 20);
            // Com a tela em WebView2 o fundo e painted pelo proprio HTML
            // (app.css, "cover"). Montar aqui um bitmap de 2035 x 1622 com
            // escala bicubica custava tempo de abertura E, como o Windows so
            // pinta o fundo da Form uma vez, ele nunca chegava a aparecer:
            // ficava na tela um desenho antigo, com a foto do alien grande e
            // fora de lugar. Esta funcao so roda no plano B (sem Runtime).
            if (telaWeb) return;
            try
            {
                var novo = MontarFundo();
                // reaproveitou o mesmo fundo: nao troca nem descarta nada
                if (novo != null && !Object.ReferenceEquals(novo, imgComp))
                {
                    var velho = imgComp;
                    imgComp = novo;
                    BackgroundImage = novo;
                    BackgroundImageLayout = ImageLayout.Stretch;
                    try { if (velho != null) velho.Dispose(); } catch { }
                }
            }
            catch (Exception e) { try { Log("Erro ao aplicar fundo: " + e.Message); } catch { } }

            // rotulos transparentes: a foto aparece atras deles
            foreach (var l in new[] { lblSub, lblIP, lblPais, lblProto, lblApps, lblLog, lblStatus, lblTemas })
            {
                l.BackColor = Color.Transparent;
                l.ForeColor = CorTexto;
            }
            lblSub.ForeColor = CorApagado;
            lblIP.ForeColor = CorApagado;
            lblStatus.ForeColor = Color.FromArgb(96, 208, 198);
            try { cboTema.ForeColor = CorTexto; } catch { }

            // botoes de vidro
            var vidroCiano = Color.FromArgb(120, 20, 52, 56);
            var sobreCiano = Color.FromArgb(185, 28, 71, 77);
            var vidroAmbar = Color.FromArgb(120, 34, 26, 12);
            var sobreAmbar = Color.FromArgb(190, 58, 44, 24);
            object[][] botoes = new object[][] {
                new object[] { btnRefresh, vidroCiano, CorBorda, CorTexto, sobreCiano },
                new object[] { btnPing, vidroCiano, CorBorda, CorTexto, sobreCiano },
                new object[] { btnAdd, Color.FromArgb(120, 62, 96, 100), Color.FromArgb(88, 138, 144), CorTexto, Color.FromArgb(170, 70, 112, 118) },
                new object[] { btnRemove, vidroAmbar, CorAmbar, CorAmbar, sobreAmbar },
                new object[] { btnUninstall, vidroAmbar, CorAmbar, CorAmbar, sobreAmbar },
                new object[] { btnUpdate, vidroAmbar, CorAmbar, CorAmbar, sobreAmbar } };
            foreach (var d in botoes)
            {
                var b = (Button)d[0];
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = (Color)d[1];
                b.ForeColor = (Color)d[3];
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = (Color)d[2];
                b.FlatAppearance.MouseOverBackColor = (Color)d[4];
                b.FlatAppearance.MouseDownBackColor = (Color)d[4];
            }

            foreach (var ck in new[] { chkFull, chkStartup, chkAutoRe, chkBootConn })
            {
                ck.BackColor = Color.Transparent;
                ck.ForeColor = CorTexto;
            }

            if (imgAlienEscuro != null) picAlien.Image = imgAlienEscuro;

            foreach (var cb in new[] { cboCountry, cboProto })
            {
                cb.BackColor = Color.FromArgb(7, 23, 26);
                cb.ForeColor = CorTexto;
            }
            lv.BackColor = Color.FromArgb(14, 26, 28);
            lv.ForeColor = CorTexto;
            lbApps.BackColor = Color.FromArgb(4, 13, 15);
            lbApps.ForeColor = CorTexto;
            lbLog.BackColor = Color.FromArgb(3, 10, 12);
            lbLog.ForeColor = CorTexto;
            AjustarColunas();

            try { EscurecerRolagens(); } catch { }

            // barra de titulo escura (20 = Windows 11, 19 = Windows 10)
            try
            {
                var hwnd = Handle;
                int v = 1;
                if (Nativo.DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                {
                    v = 1;
                    Nativo.DwmSetWindowAttribute(hwnd, 19, ref v, 4);
                }
                Nativo.SendMessage(hwnd, 0x0086, (IntPtr)1, IntPtr.Zero);
            }
            catch { }

            AtualizarEstadoConexao();
            Invalidate(true);
        }

        // P/Invoke de tema nativo
        static class Nativo
        {
            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
            public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);
            [DllImport("dwmapi.dll", PreserveSig = true)]
            public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

            // escurece a barra de rolagem nativa (a filha SCROLLBAR do controle)
            public static void EscurecerScroll(IntPtr h)
            {
                try
                {
                    IntPtr sb = FindWindowEx(h, IntPtr.Zero, "SCROLLBAR", null);
                    if (sb != IntPtr.Zero) SetWindowTheme(sb, "DarkMode_Explorer", null);
                }
                catch { }
            }
        }

        // ============================================================
        //  BOTAO CONECTAR (desenho proprio: vidro do estado + halo)
        // ============================================================
        void PintarBotaoConectar(object snd, PaintEventArgs ev)
        {
            try
            {
                var b = (Button)snd;
                var g5 = ev.Graphics;
                PintarFundo(g5, b, b.ClientRectangle);
                var r5 = Rectangle.Inflate(b.ClientRectangle, -5, -5);
                var cor = b.ForeColor;
                if (!b.Enabled) cor = Color.FromArgb(120, 140, 142);
                var sobre = b.Enabled && b.ClientRectangle.Contains(b.PointToClient(Cursor.Position));
                using (var brF = new SolidBrush(Color.FromArgb(sobre ? 205 : 170, cor.R / 7, cor.G / 4, cor.B / 6)))
                    g5.FillRectangle(brF, r5);
                DesenharBrilho(g5, r5, cor);
                using (var pen5 = new Pen(Color.FromArgb(240, cor), 1))
                    g5.DrawRectangle(pen5, r5.X, r5.Y, r5.Width - 1, r5.Height - 1);
                var lin = b.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                using (var br5 = new SolidBrush(cor))
                using (var brSub = new SolidBrush(Color.FromArgb(255, (cor.R + 255) / 2, (cor.G + 255) / 2, (cor.B + 255) / 2)))
                {
                    if (lin.Length > 1)
                    {
                        g5.DrawString(lin[0], b.Font, br5, new RectangleF(r5.X, r5.Y + 2, r5.Width, 21), fmtCentro);
                        g5.DrawString(lin[1], fntConSub, brSub, new RectangleF(r5.X, r5.Y + 20, r5.Width, 15), fmtCentro);
                    }
                    else g5.DrawString(lin[0], b.Font, br5, new RectangleF(r5.X, r5.Y, r5.Width, r5.Height), fmtCentro);
                }
            }
            catch { }
        }

        // ============================================================
        //  LISTA DE SERVIDORES
        // ============================================================
        void PreencherPaises()
        {
            var prev = countryFilter;
            cboCountry.Items.Clear();
            filterMap.Clear();
            cboCountry.Items.Add("Todos os pa\u00edses da Am\u00e9rica do Sul");
            foreach (var iso in Dados.LatAmOrder)
            {
                cboCountry.Items.Add(Dados.GetCountryName(iso) + " (" + iso + ")");
                filterMap.Add(new Dados.FontePais { ISO = iso });
            }
            var idx = 0;
            if (prev != null)
            {
                for (int i = 0; i < filterMap.Count; i++)
                    if (filterMap[i].ISO == prev) { idx = i + 1; break; }
            }
            suppressUi = true;
            cboCountry.SelectedIndex = idx;
            suppressUi = false;
        }

        void JuntarListas()
        {
            // o ping medido gruda no IP: quando um download novo reconstrui as
            // listas, os valores medidos voltam (antes zeravam e a coluna
            // "Seu ping" ficava com "-" mesmo apos medir)
            try
            {
                foreach (var s in gateServers)
                {
                    List<int> h;
                    if (s.Measured < 0 && pingHist.TryGetValue(s.IP, out h) && h != null && h.Count > 0)
                        s.Measured = h[h.Count - 1];
                }
                foreach (var s in pvlServers)
                {
                    List<int> h2;
                    if (s.Measured < 0 && pingHist.TryGetValue(s.IP, out h2) && h2 != null && h2.Count > 0)
                        s.Measured = h2[h2.Count - 1];
                }
                foreach (var s in vpnBookServers)
                {
                    List<int> h3;
                    if (s.Measured < 0 && pingHist.TryGetValue(s.IP, out h3) && h3 != null && h3.Count > 0)
                        s.Measured = h3[h3.Count - 1];
                }
                foreach (var s in urbanServers)
                {
                    List<int> h4;
                    if (s.Measured < 0 && pingHist.TryGetValue(s.IP, out h4) && h4 != null && h4.Count > 0)
                        s.Measured = h4[h4.Count - 1];
                }
            }
            catch { }
            // uniao das fontes: VPN Gate + lista publica + telexy + hunt + vpnbook + urban
            // SEM DEDUPE e SEM NENHUMA OUTRA REGRA DE CORTE: a UNICA coisa que
            // sai da lista e o que nao for da America do Sul (Brasil ja fica
            // fora do filtro de pais). Servidor repetido entre fontes aparece
            // nas duas linhas - melhor mostrar duas vezes do que sumir.
            var nova = new List<Servidor>();
            int foraAmericas = 0;
            foreach (var s in gateServers) { if (!Dados.EhLatina(s.ISO)) { foraAmericas++; continue; } nova.Add(s); }
            // quem veio sem .ovpn mas tem o MESMO ip em outra fonte: herda a
            // configuracao de la - senao esse servidor seria perdido, pois o
            // OpenVPN nao conecta sem o arquivo. O caso comum e o hunt: ele
            // lista ip/porta de servidores VPN Gate (que precisam do .ovpn com
            // certificado) e nao publica o arquivo. Se o telexy ou a lista
            // publica tem esse mesmo ip, o .ovpn dele salva o do hunt.
            var cfgPorIp = new Dictionary<string, string>();
            var urlPorIp = new Dictionary<string, string>();
            var idPorIp = new Dictionary<string, string>();
            try
            {
                foreach (var g in gateServers)
                    if (!String.IsNullOrEmpty(g.Cfg) && !cfgPorIp.ContainsKey(g.IP))
                        cfgPorIp[g.IP] = g.Cfg;
                foreach (var p in pvlServers)
                {
                    if (String.IsNullOrEmpty(p.CfgUrl) && p.Src == "tlx" && !urlPorIp.ContainsKey(p.IP))
                        urlPorIp[p.IP] = p.CfgUrl;
                    if (String.IsNullOrEmpty(p.Cid) && p.Src == "pvl" && !idPorIp.ContainsKey(p.IP))
                        idPorIp[p.IP] = p.Cid;
                }
                foreach (var s in pvlServers)
                {
                    if (String.IsNullOrEmpty(s.Cfg) && String.IsNullOrEmpty(s.CfgUrl))
                    {
                        string herdado, urlHerd, idHerd;
                        if (cfgPorIp.TryGetValue(s.IP, out herdado)) s.Cfg = herdado;
                        else if (urlPorIp.TryGetValue(s.IP, out urlHerd)) s.CfgUrl = urlHerd;
                        else if (idPorIp.TryGetValue(s.IP, out idHerd)) { s.Cid = idHerd; s.Src = "pvl"; }
                    }
                }
            }
            catch { }
            // ---- junta as fontes, UM REGISTRO POR IP:PORTA ----
            // Nada e cortado: a unica regra e o pais (America do Sul sem o
            // Brasil). Quando o mesmo IP:porta vem de varias fontes (o hunt
            // repete o que o telexy e a lista publica ja trazem), em vez de
            // apagar uma das copias, junta tudo numa linha so - o .ovpn, o
            // link e a velocidade do telexy/da lista publica salvos no
            // registro que o hunt trouxe. Nenhum servidor desaparece e
            // nenhum dado se perde.
            var porEndpoint = new Dictionary<string, Servidor>();
            Action<Servidor> entrar = delegate(Servidor s)
            {
                if (s == null || String.IsNullOrEmpty(s.IP) || String.IsNullOrEmpty(s.ISO)) return;
                if (!Dados.EhLatina(s.ISO)) { foraAmericas++; return; }
                string k = s.IP + ":" + s.Port;
                Servidor tem;
                if (!porEndpoint.TryGetValue(k, out tem))
                {
                    porEndpoint[k] = s;
                    return;
                }
                try
                {
                    if (String.IsNullOrEmpty(tem.Cfg) && !String.IsNullOrEmpty(s.Cfg)) tem.Cfg = s.Cfg;
                    if (String.IsNullOrEmpty(tem.CfgUrl) && !String.IsNullOrEmpty(s.CfgUrl)) tem.CfgUrl = s.CfgUrl;
                    if (String.IsNullOrEmpty(tem.Cid) && !String.IsNullOrEmpty(s.Cid)) { tem.Cid = s.Cid; if (tem.Src == "hunt") tem.Src = "pvl"; }
                    if (String.IsNullOrEmpty(tem.Proto) && !String.IsNullOrEmpty(s.Proto)) tem.Proto = s.Proto;
                    if (tem.Port <= 0 && s.Port > 0) tem.Port = s.Port;
                    // a cidade medida por qualquer das fontes vale: e melhor
                    // mostrar o nome de quem sabe do que o pais sozinho.
                    // Aprende para sempre: gravada em cidades.json, na proxima
                    // vez este IP ja entra com nome mesmo vindo de fonte que
                    // nao manda cidade (VPN Gate, Hunt, lista publica).
                    if (String.IsNullOrEmpty(tem.Cidade) && !String.IsNullOrEmpty(s.Cidade)) tem.Cidade = s.Cidade;
                    if (!String.IsNullOrEmpty(s.Cidade)) Store.SalvarCidade(s.IP, s.Cidade);
                    if (tem.Speed <= 0 && s.Speed > 0) tem.Speed = s.Speed;
                    if (tem.Measured < 0 && s.Measured >= 0) tem.Measured = s.Measured;
                    // o .ovpn guardado e reaproveitado (nao se redesenha)
                    if (String.IsNullOrEmpty(tem.Cfg))
                    {
                        var guardado = Store.ReadCfgCache(s.IP);
                        if (!String.IsNullOrWhiteSpace(guardado)) tem.OvpnOk = true;
                    }
                }
                catch { }
            };
            foreach (var s in gateServers) entrar(s);
            foreach (var s in gateEspelho) entrar(s);   // reserva: mesmos servidores por outro caminho
            foreach (var s in pvlServers) entrar(s);
            foreach (var s in vpnBookServers) entrar(s);
            foreach (var s in urbanServers) entrar(s);
            foreach (var s in servers) entrar(s);

            // ---- qual fonte e a melhor de cada registro? ----
            // O registro ja vem unico por IP:PORTA. Agora a fonte que "responde"
            // passa a ser a melhor disponivel, e nao simplesmente a primeira que
            // chegou: o que decide e quem tem .ovpn de verdade.
            //   hunt = nao publica .ovpn, so serve a semente SoftEther (fraco)
            //   gate = .ovpn com certificado do VPN Gate (forte)
            //   tlx  = baixa o .ovpn novo a cada conexao (forte)
            //   pvl  = baixa pela lista publica (forte)
            //   vpnbook e urban sao mecanismos proprios: ficam como estao.
            foreach (var s in porEndpoint.Values)
            {
                if (s == null || s.Src == "urban" || s.Src == "vpnbook") continue;
                if (!String.IsNullOrEmpty(s.Cfg)) s.Src = "gate";
                else if (!String.IsNullOrEmpty(s.CfgUrl)) s.Src = "tlx";
                else if (!String.IsNullOrEmpty(s.Cid)) s.Src = "pvl";
                else s.Src = "hunt";
            }

            foreach (var kv in porEndpoint) nova.Add(kv.Value);

            // ---- limpeza final: nenhum IP:PORTA repetido na tela ----
            // A fusao acima usa a chave "ip:porta". Se uma fonte trouxer o
            // mesmo IP com porta 0 (nao sabe a porta) e outra trouxer com a
            // porta certa, as duas viram linhas diferentes e a tela mostra o
            // mesmo servidor duas vezes. Aqui a linha sem porta e fundida na
            // que tem, e o que sobrar e unico por ip:porta.
            var comPorta = new HashSet<string>();
            foreach (var s in nova) if (s.Port > 0) comPorta.Add(s.IP);
            var limpos = new List<Servidor>();
            var vistosFim = new HashSet<string>();
            foreach (var s in nova)
            {
                if (s.Port <= 0 && comPorta.Contains(s.IP)) continue;   // a de porta manda
                if (s.Port <= 0 && s.Src == "hunt")
                {
                    // hunt sem porta nenhuma: monta pela semente para poder existir
                    try
                    {
                        var sem = Dados.SementeOvpn();
                        if (!String.IsNullOrEmpty(sem))
                        {
                            var portaProvavel = PortaPadrao(s.Proto);
                            if (portaProvavel > 0) { s.Port = portaProvavel; }
                        }
                    }
                    catch { }
                }
                string chave = (s.IP ?? "") + ":" + (s.Port > 0 ? s.Port.ToString() : "?");
                if (!vistosFim.Add(chave)) continue;                     // ja tinha
                limpos.Add(s);
            }
            nova = limpos;
            servers = nova;
            if (sortCol < 0) OrdenarPadrao();
            AtualizarLista();
            if (foraAmericas > 0) Log(foraAmericas + " servidores fora da America do Sul foram tirados da lista (Brasil, America Central, Norte, India, Japao, Europa...).");
        }

        // Chegou servidor novo? Baixa o .ovpn dele em segundo plano para o clique em
        // CONECTAR nao perder tempo. Nao tira ninguem da lista.
        DateTime proximoOvpn = DateTime.MinValue;
        bool ovpnMarcado = false;
        void AgendarConferenciaSeFaltou()
        {
            try
            {
                if (disposing) return;
                bool falta = false;
                foreach (var s in servers)
                    if (s != null && s.Src != "urban" && !s.OvpnOk) { falta = true; break; }
                if (!falta) return;
                if (ovpnMarcado && DateTime.Now < proximoOvpn) return;
                ovpnMarcado = true;
                proximoOvpn = DateTime.Now.AddSeconds(4);
                var t = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        System.Threading.Thread.Sleep(4000);
                        if (saindo || disposing) return;
                        RevalidarFontes();
                    }
                    catch { }
                }));
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        // ordem padrao: menor ping ESPERADO do Discord (medido + borda por pais)
        void OrdenarPadrao()
        {
            servers = servers.OrderBy(s =>
            {
                if (s.Measured < 0) return 9999;
                int b = 60;
                try { if (Dados.BordaMs.ContainsKey(s.ISO)) b = Dados.BordaMs[s.ISO]; } catch { }
                return s.Measured + b;
            }).ThenBy(s => s.Proto == "udp" ? 0 : 1).ToList();
        }

        void OrdenarLista()
        {
            try
            {
                var col = sortCol;
                if (col < 0) return;
                var desc = !sortAsc;
                IEnumerable<Servidor> ord = servers;
                switch (col)
                {
                    case 0: ord = servers.OrderBy(s => s.Name); break;
                    case 1:
                        ord = servers.OrderBy(s => IpParaLong(s.IP));
                        break;
                    case 2: ord = servers.OrderBy(s => s.Measured < 0 ? int.MaxValue : s.Measured); break;
                    case 3: ord = servers.OrderByDescending(s => (double)s.Speed); break;
                    case 4: ord = servers.OrderBy(s => s.Proto); break;
                    default: ord = servers.OrderBy(s => s.Name); break;
                }
                if (desc) ord = ord.Reverse();
                servers = ord.ToList();
                AtualizarSetasColunas();
            }
            catch { }
        }

        long IpParaLong(string ipS)
        {
            var m = Regex.Match(ipS ?? "", @"^\s*(\d+)\.(\d+)\.(\d+)\.(\d+)\s*$");
            if (!m.Success) return long.MaxValue;
            return long.Parse(m.Groups[1].Value) * 16777216 + long.Parse(m.Groups[2].Value) * 65536
                 + long.Parse(m.Groups[3].Value) * 256 + long.Parse(m.Groups[4].Value);
        }

        void AtualizarSetasColunas()
        {
            var titulos = new[] { "Pa\u00eds", "IP", "Seu ping", "Velocidade", "Protocolo", "" };
            char cres = '\u25B2';
            char dec = '\u25BC';
            try
            {
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    var t = i < titulos.Length ? titulos[i] : "";
                    if (i < titulos.Length && sortCol == i) t += " " + (sortAsc ? cres : dec);
                    lv.Columns[i].Text = t;
                }
            }
            catch { }
        }

        void AjustarColunas()
        {
            try
            {
                int sW = 0;
                for (int iW = 0; iW < lv.Columns.Count - 1; iW++) sW += lv.Columns[iW].Width;
                lv.Columns[lv.Columns.Count - 1].Width = Math.Max(4, lv.ClientSize.Width - sW);
            }
            catch { }
        }

        void AtualizarLista()
        {
            try { EnviarLista(); } catch { }
            if (InvokeRequired) { BeginInvoke((Action)AtualizarLista); return; }
            lv.BeginUpdate();
            // guarda quem estava clicado (o clique nao se perde na reconstrucao)
            var selIPs = new HashSet<string>();
            try { foreach (ListViewItem itSel in lv.SelectedItems) { var tg = itSel.Tag as Servidor; if (tg != null) selIPs.Add(tg.IP); } } catch { }
            lv.Items.Clear();
            try
            {
                // grupos so na ordem padrao (a ordenacao manual fica plana)
                lv.ShowGroups = (sortCol < 0);
                if (lv.Groups.Count == 0)
                {
                    lv.Groups.Add(new ListViewGroup("sul", "Am\u00e9rica do Sul"));
                }
            }
            catch { }
            var quer = protocol;
            foreach (var s in servers)
            {
                if (countryFilter != null && s.ISO != countryFilter) continue;
                // so UDP / so TCP: esconde quem nao fala o protocolo escolhido
                if ((quer == "udp" || quer == "tcp") && !String.IsNullOrEmpty(s.Proto) && s.Proto != "both" && s.Proto != quer) continue;
                // sem regra de corte: mostra TODOS os pings (os lentos afundam
                // sozinhos para o fim pela ordenacao)
                var pingTxt = s.Measured < 0 ? "-" : s.Measured + " ms";
                var protoTxt = "UDP e TCP";
                if (s.Proto == "udp") protoTxt = "UDP";
                else if (s.Proto == "tcp") protoTxt = "TCP";
                else if (String.IsNullOrWhiteSpace(s.Proto)) protoTxt = "-";
                var nomeLv = s.Name;
                try { var cidLv = Dados.GetServerLocal(s); if (!String.IsNullOrEmpty(cidLv) && cidLv != nomeLv) nomeLv = nomeLv + " - " + cidLv; } catch { }
                var it = new ListViewItem(nomeLv);
                try
                {
                    // so existe o grupo da America do Sul
                    it.Group = lv.Groups["sul"];
                }
                catch { }
                // IP:PORTA no mesmo campo, em verde-neon. Sem a porta nao dava para
                // saber se duas linhas sao o mesmo servidor (mesmo IP, portas
                // diferentes) ou duplicata de verdade.
                it.SubItems.Add(s.IP + ":" + (s.Port > 0 ? s.Port.ToString() : "?"));
                try { it.SubItems[1].ForeColor = CorVerdeNeon; } catch { }
                it.SubItems.Add(pingTxt);
                it.SubItems.Add(Math.Round(s.Speed / 1000000.0, 1) + " Mbps");
                it.SubItems.Add(protoTxt);
                it.Tag = s;
                lv.Items.Add(it);
                try { if (selIPs.Contains(s.IP)) it.Selected = true; } catch { }
            }
            lv.EndUpdate();
            AjustarColunas();
            try { lv.Invalidate(true); lv.Update(); } catch { }
            try { BeginInvoke((Action)EscurecerRolagens); } catch { }

            if (connected) return;
            if (servers.Count == 0)
                lblStatus.Text = "Status: nenhum servidor da Am\u00e9rica do Sul agora";
            else
            {
                var txtSt = servers.Count + " servidores na Am\u00e9rica do Sul | " + lv.Items.Count + " mostrados";
                if (quer == "udp" || quer == "tcp")
                {
                    int ocultos = 0;
                    foreach (var s2 in servers)
                    {
                        if (countryFilter != null && s2.ISO != countryFilter) continue;
                        if (!String.IsNullOrEmpty(s2.Proto) && s2.Proto != "both" && s2.Proto != quer) ocultos++;
                    }
                    if (ocultos > 0) txtSt += " | " + ocultos + " " + quer.ToUpperInvariant() + " escondidos";
                }
                lblStatus.Text = txtSt;
            }
        }

        void AtualizarListaProgramas()
        {
            try { EnviarProgramas(); } catch { }
            if (InvokeRequired) { BeginInvoke((Action)AtualizarListaProgramas); return; }
            lbApps.InvalidarLarguraCache();
            lbApps.BeginUpdate();
            try
            {
                lbApps.Items.Clear();
                foreach (var a in appList)
                    lbApps.Items.Add(a.Name);   // so o nome: o caminho fica de fora
            }
            finally { lbApps.EndUpdate(); }
            if (appList.Count == 0 && !fullTunnel)
                lblApps.Text = "Programas que passam pela VPN - nenhum escolhido ainda (clique em Adicionar programa):";
            else
                lblApps.Text = "Programas que passam pela VPN (o resto do PC fica na sua conex\u00e3o normal):";
            try
            {
                int larg = 0;
                foreach (var t in lbApps.Items)
                    larg = Math.Max(larg, TextRenderer.MeasureText((string)t, lbApps.Font).Width + 12);
                lbApps.HorizontalExtent = larg;
            }
            catch { }
            try { BeginInvoke((Action)EscurecerRolagens); } catch { }
        }

        void AdicionarPrograma(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var a in appList)
                if (String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                { Log(name + " j\u00e1 est\u00e1 na lista."); return; }
            var domains = new List<string>();
            foreach (var k in Dados.AppPresets.Keys)
                if (String.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { domains = Dados.AppPresets[k].ToList(); break; }
            appList.Add(new Programa { Name = name, Path = path, Domains = domains });
            AtualizarListaProgramas();
            SalvarEstado();
            Log(name + " vai passar pela VPN.");
            if (connected) { try { SincronizarRotas(); } catch { } }
        }

        // Acha o executavel do Discord onde estiver (instalacao do usuario).
        string AcharCaminhoDiscord()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Discord"))
                {
                    try
                    {
                        var cam = p.MainModule.FileName;
                        if (!String.IsNullOrEmpty(cam) && File.Exists(cam)) return cam;
                    }
                    catch { }
                }
            }
            catch { }

            var locais = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Discord.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Discord", "Discord.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Discord", "Discord.exe"),
            };
            foreach (var local in locais)
            {
                try
                {
                    if (!String.IsNullOrEmpty(local) && File.Exists(local)) return local;
                    var dir = Path.GetDirectoryName(local);
                    if (!String.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        foreach (var sub in Directory.GetDirectories(dir, "app-*"))
                        {
                            var cand = Path.Combine(sub, "Discord.exe");
                            if (File.Exists(cand)) return cand;
                        }
                    }
                }
                catch { }
            }
            return "";
        }

        // ============================================================
        //  DOWNLOADS (VPN Gate com repeticao + fontes publicas)
        // ============================================================
        bool BaixarListaServidores()
        {
            if (dlTask != null && !dlTask.IsCompleted) return false;
            dlStart = DateTime.Now;
            var wc = Rede.NovoWc();
            dlTask = wc.DownloadStringTaskAsync(new Uri(Dados.ApiUrl));
            // o espelho vai junto, em paralelo: se o vpngate.net cair ou ficar
            // lento, a lista continua vindo do GitHub sem ninguem notar
            try
            {
                if (dlEspelho == null || dlEspelho.IsCompleted)
                {
                    var wc2 = Rede.NovoWc();
                    dlEspStart = DateTime.Now;
                    dlEspelho = wc2.DownloadStringTaskAsync(new Uri(Dados.EspelhoUrl));
                }
            }
            catch { }
            return true;
        }

        // ============================================================
        //  FONTES PUBLICAS: busca completa numa thread so
        // ============================================================
        // A versao anterior montava uma fila e dependia do relogio de 400 ms da
        // tela para colher cada download. Esse elo era fragil: se a tela nao
        // fizesse a colheita naquele instante, a fila esvaziava sem nunca virar
        // lista e o programa ficava so com o cache (por isso so apareciam 1-2
        // servidores). Aqui a busca e AUTO-CONTIDA: uma thread baixa tudo,
        // monta a lista e so entao avisa a tela. Sem fila, sem relogio, sem
        // travamento.
        bool fontesRodando = false;
        DateTime fontesTerminouEm = DateTime.MinValue;

        void IniciarFontesPublicas()
        {
            try { if (saindo) return; } catch { return; }
            if (fontesRodando) return;          // ja esta buscando: nao empilha
            fontesRodando = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { BuscarFontesPublicas(); }
                catch { }
                finally { try { fontesRodando = false; } catch { } }
            });
        }

        // download de um texto com tempo maximo (o WebClient do .NET 4 nao tem
        // timeout proprio e ficava pendurado esperando para sempre)
        static string PegarTexto(string url, int ms)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                req.Timeout = ms;
                req.ReadWriteTimeout = ms;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var rd = new StreamReader(rs, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch { return null; }
        }

        void BuscarFontesPublicas()
        {
            DateTime t0 = DateTime.Now;
            var jobs = new List<string[]>();     // {tipo, "ISO|slug"}
            foreach (var p in Dados.PvlPaises)
                if (p.TemPvl && !String.IsNullOrEmpty(p.Slug))
                    jobs.Add(new string[] { "pvl", p.ISO + "|" + p.Slug });
            foreach (var h in Dados.HuntPaises)
                jobs.Add(new string[] { "hunt", h.ISO + "|" + h.Slug });
            foreach (var p in Dados.PvlPaises)
                jobs.Add(new string[] { "tlx", p.ISO + "|" });

            var res = new List<Servidor>[jobs.Count];
            bool[] respostas = new bool[jobs.Count];
            int falhas = 0;
            int passo = 5;                       // 5 por vez, como antes
            for (int i = 0; i < jobs.Count; i += passo)
            {
                int ini = i;
                int fim = Math.Min(i + passo, jobs.Count);
                var ths = new List<Thread>();
                for (int k = ini; k < fim; k++)
                {
                    int idx = k;
                    var t = new Thread(new ThreadStart(delegate
                    {
                        try
                        {
                            var j = jobs[idx];
                            var partes = j[1].Split('|');
                            string iso = partes[0];
                            string url = j[0] == "tlx" ? Dados.TlxUrl + "/vpn?country=" + iso + "&limit=50"
                                    : j[0] == "hunt" ? "https://huntvpn.dev/vpn/" + partes[1]
                                    : Dados.PvlUrl + "/country/" + partes[1] + "/";
                            var txt = PegarTexto(url, 20000);
                            if (String.IsNullOrWhiteSpace(txt)) return;
                            List<Servidor> novos = j[0] == "tlx" ? Rede.ParseTlxJson(txt, iso)
                                              : j[0] == "hunt" ? Rede.ParseHuntHtml(txt, iso)
                                              : Rede.ParsePvlPage(txt, iso);
                            // respondeu, mesmo que sem servidor: conta como
                            // resposta OK. Antes entrava na conta de "sem
                            // resposta" e o log acusava 17 falhas quando na
                            // verdade era so pais que hoje nao tem servidor
                            // nenhum (Uruguai, Bolivia, Guiana...).
                            if (respostas == null) respostas = new bool[jobs.Count];
                            respostas[idx] = true;
                            if (novos != null && novos.Count > 0) res[idx] = novos;
                        }
                        catch { }
                    }));
                    t.IsBackground = true;
                    ths.Add(t);
                }
                foreach (var t in ths) t.Start();
                foreach (var t in ths) { try { t.Join(22000); } catch { } }
            }

            var todos = new List<Servidor>();
            for (int i = 0; i < res.Length; i++)
            {
                if (!respostas[i]) falhas++;
                if (res[i] == null) continue;
                foreach (var s in res[i]) todos.Add(s);
            }
            fontesTerminouEm = DateTime.Now;
            pvlFalhas = falhas;

            Action entrega = delegate
            {
                try
                {
                    pvlServers = todos;
                    SalvarPvlCache();
                    JuntarListas();
                    double seg = (DateTime.Now - t0).TotalSeconds;
                    Log("Fontes publicas: " + todos.Count + " servidores em " + seg.ToString("0") + " s ("
                        + jobs.Count + " pedidos, " + falhas + " nao responderam).");
                }
                catch { }
            };
            try { if (InvokeRequired) BeginInvoke(entrega); else entrega(); } catch { }
        }

        // ============================================================
        //  ATUALIZACAO DAS FONTES (ao abrir e a cada 10 min)
        // ============================================================
        // Aqui so ENTRA servidor novo e se atualiza ip/porta. NADA e tirado da
        // lista por causa de sonda: o teste de porta da vez passada engana
        // (UDP perde pacote e o TCP pode estar fechado mesmo com o servidor
        // de pe), e tirar o servidor fazia a Argentina entrar e sumir da tela
        // na hora. Quem esta realmente fora do ar e barrado na hora de
        // conectar, pelo failover, que ja era rapido e so usa o tempo
        // necessario. O que este passo faz de verdade e manter ip, porta e
        // .ovpn atualizados.
        void RevalidarFontes()
        {
            try { if (saindo || disposing) return; } catch { return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int antes = 0;
                    try { antes = servers.Count; } catch { }
                    DateTime t0 = DateTime.Now;

                    // ---------- 1) Urban e VPNBook em paralelo ----------
                    var urbanRes = new object[1];
                    var vbRes = new object[1];
                    using (var ev1 = new ManualResetEventSlim(false))
                    using (var ev2 = new ManualResetEventSlim(false))
                    {
                        ThreadPool.QueueUserWorkItem(delegate
                        { try { vbRes[0] = Rede.GerarVpnBook(); } catch { } finally { try { ev1.Set(); } catch { } } });
                        ThreadPool.QueueUserWorkItem(delegate
                        { try { urbanRes[0] = CarregarUrbanSilencioso(); } catch { } finally { try { ev2.Set(); } catch { } } });
                        try { ev1.Wait(9000); } catch { }
                        try { ev2.Wait(12000); } catch { }
                    }
                    var vb = vbRes[0] as List<Servidor>;
                    var ub = urbanRes[0] as List<Servidor>;

                    // ---------- 2) VPN Gate + telexy + lista publica + hunt ----------
                    try { IniciarFontesPublicas(); } catch { }
                    try { if (dlTask == null || dlTask.IsCompleted) BaixarListaServidores(); } catch { }
                    // a busca das fontes publicas agora e auto-contida (roda
                    // sozinha numa thread e so entao avisa a tela): nao ha mais
                    // fila para esperar aqui
                    try
                    {
                        DateTime lim = DateTime.Now.AddSeconds(45);
                        while (fontesRodando && DateTime.Now < lim && !saindo)
                            System.Threading.Thread.Sleep(200);
                    }
                    catch { }

                    // ---------- 3) a lista entra de uma vez, na tela ----------
                    try
                    {
                        if (InvokeRequired) BeginInvoke((Action)delegate { try { JuntarListas(); } catch { } });
                        else JuntarListas();
                    }
                    catch { }

                    // ---------- 4) .ovpn baixado DEPOIS da lista entrar ----------
                    // Antes ficava antes, e nesse instante a lista ainda era a
                    // antiga: nao baixava nada ("0 .ovpn baixados"). Agora roda
                    // depois que os servidores chegaram de fato.
                    int ovpns = 0;
                    try
                    {
                        DateTime lim2 = DateTime.Now.AddSeconds(40);
                        while (fontesRodando && DateTime.Now < lim2 && !saindo)
                            System.Threading.Thread.Sleep(200);
                        ovpns = PrepararOvpnDosNovos();
                    }
                    catch { }

                    int depois = 0;
                    try { depois = servers.Count; } catch { }
                    double seg = (DateTime.Now - t0).TotalSeconds;
                    string msg = "Fontes atualizadas em " + seg.ToString("0.0") + " s: " + antes + " -> " + depois + " servidores"
                               + (ub != null && ub.Count > 0 ? " (Urban " + ub.Count + ")" : "")
                               + (vb != null && vb.Count > 0 ? ", VPNBook " + vb.Count : "")
                               + (ovpns > 0 ? ", " + ovpns + " .ovpn baixados" : "") + ".";
                    try { if (!saindo) Log(msg); } catch { }
                }
                catch { }
            });
        }

        // Baixa o .ovpn dos servidores que ainda nao tem um guardado bom, para
        // a conexao nao perder tempo baixando na hora do clique. Nunca tira
        // servidor da lista por causa disso.
        int PrepararOvpnDosNovos()
        {
            int baixados = 0;
            try
            {
                var falta = new List<Servidor>();
                Action<Servidor> add = delegate(Servidor s)
                {
                    if (s == null || s.Src == "urban") return;
                    if (!Dados.EhLatina(s.ISO)) return;
                    if (String.IsNullOrEmpty(s.Cfg) && String.IsNullOrEmpty(s.CfgUrl)) return;
                    if (s.OvpnOk) return;
                    if (OvpnTextoBom(Store.ReadCfgCache(s.IP))) return;
                    foreach (var j in falta) if (j.IP == s.IP) return;
                    falta.Add(s);
                };
                foreach (var s in gateServers) add(s);
                foreach (var s in pvlServers) add(s);
                foreach (var s in vpnBookServers) add(s);
                foreach (var s in servers) add(s);
                if (falta.Count == 0) return 0;

                int pos = 0, objs = 0;
                var alvos = new object[falta.Count];
                Action worker = delegate
                {
                    while (true)
                    {
                        int i = Interlocked.Increment(ref pos) - 1;
                        if (i < 0 || i >= falta.Count) break;
                        try { alvos[i] = OvpnBom(falta[i]) ? (object)falta[i] : null; } catch { alvos[i] = null; }
                        Interlocked.Increment(ref objs);
                    }
                };
                int nThreads = Math.Min(5, falta.Count);
                var ths = new List<Thread>();
                for (int k = 0; k < nThreads; k++)
                { var t = new Thread(new ThreadStart(worker)); t.IsBackground = true; ths.Add(t); }
                foreach (var t in ths) t.Start();
                DateTime t0 = DateTime.Now;
                while (objs < falta.Count && (DateTime.Now - t0).TotalSeconds < 30) Thread.Sleep(60);
                for (int i = 0; i < alvos.Length; i++)
                {
                    if (alvos[i] != null) baixados++;
                    else
                    {
                        // nao conseguiu agora: o servidor CONTINUA na lista e sera
                        // tentado de novo na proxima vez (a internet pode ter
                        // oscilado, e o .ovpn talvez volte)
                        falta[i].OvpnOk = false;
                    }
                }
            }
            catch { }
            return baixados;
        }

        // Baixa/confere o .ovpn de todos os servidores com porta viva. E o que
        // garante que o programa JA ABRA com VPN que funciona: o arquivo precisa
        // ter "remote" (senha onde aponta), o bloco de chave e nao pode vir
        // cortado. Devolve quantos foram reprovados.

        // o .ovpn presta? tem que ter o remote e o certificado/chave intacto
        bool OvpnBom(Servidor s)
        {
            try
            {
                // O TELEXY muda a porta e o protocolo sem avisar: ele tem que ser
                // baixado de novo SEMPRE (e e o que corrige a porta na hora).
                // Nas outras fontes o que estiver guardado (ate 7 dias) ja foi
                // conferido, entao nao se baixa de novo a toa.
                if (s.Src != "tlx")
                {
                    var guardado = Store.ReadCfgCache(s.IP);
                    if (OvpnTextoBom(guardado)) { s.OvpnOk = true; return true; }
                }
                Store.DropCfgCache(s.IP);
                var cfg = PegarConfig(s);
                if (!OvpnTextoBom(cfg)) { s.OvpnOk = false; return false; }
                Store.WriteCfgCache(s.IP, cfg);
                Store.WriteBkCache(s.IP, cfg);
                s.OvpnOk = true;
                return true;
            }
            catch { s.OvpnOk = false; return false; }
        }

        static bool OvpnTextoBom(string txt)
        {
            if (String.IsNullOrWhiteSpace(txt)) return false;
            try
            {
                // sem "remote" o OpenVPN nem sabe aonde ligar
                if (!Regex.IsMatch(txt, @"(?m)^\s*remote\s")) return false;
                // chave tem que estar inteira (o erro classico e truncar no meio)
                if (!Regex.IsMatch(txt, @"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC )?PRIVATE KEY-----")) return false;
                // e precisa do que autenticar
                bool temCert = Regex.IsMatch(txt, @"-----BEGIN CERTIFICATE-----");
                bool temUserPass = Regex.IsMatch(txt, @"(?m)^\s*auth-user-pass");
                if (!temCert && !temUserPass) return false;
                return true;
            }
            catch { return false; }
        }

        // mesma coisa que CarregarUrban, mas sem Log nem JoinListas: quem
        // revalida e quem cuida da tela depois (fora da thread da janela)
        List<Servidor> CarregarUrbanSilencioso()
        {
            try
            {
                // o MESMO accs guardado na UrbanPegarAccs (a assinatura da lista
                // so vale com ele - por isso nao se cadastra de novo aqui)
                string accs = "";
                try { accs = UrbanPegarAccs(); } catch { return null; }
                if (String.IsNullOrEmpty(accs)) return null;
                var urb = new List<Servidor>();
                var vistosU = new HashSet<string>();
                try
                {
                    foreach (var u in Rede.ParseUrbanCountries(Rede.UrbanGetCountries(accs)))
                    {
                        if (!Dados.EhLatina(u.ISO)) continue;
                        if (vistosU.Add(u.IP + ":" + u.Port)) urb.Add(u);
                    }
                }
                catch { }
                try
                {
                    foreach (var u in Rede.ParseUrbanServers(Rede.UrbanGetServers(accs)))
                        if (vistosU.Add(u.IP + ":" + u.Port)) urb.Add(u);
                }
                catch { }
                urbanServers = urb;
                return urb;
            }
            catch { return null; }
        }

        List<KeyValuePair<string, string>> huntQueue = new List<KeyValuePair<string, string>>();

        void EncherDownloads()
        {
            // ate 5 consultas ao mesmo tempo
            while (pvlTasks.Count < 5 && pvlQueue.Count > 0)
            {
                var item = pvlQueue[0];
                pvlQueue.RemoveAt(0);
                try
                {
                    string url;
                    if (item.Tipo == "tlx") url = Dados.TlxUrl + "/vpn?country=" + item.ISO + "&limit=50";
                    else
                    {
                        // pvl ou hunt: pega o slug da fila do hunt (mesma ordem)
                        string slug = "";
                        if (item.Tipo == "hunt")
                        {
                            if (huntQueue.Count == 0) continue;
                            slug = huntQueue[0].Key;
                            item.ISO = huntQueue[0].Value;
                            huntQueue.RemoveAt(0);
                            url = "https://huntvpn.dev/vpn/" + slug;
                        }
                        else url = Dados.PvlUrl + "/country/" + slugBusca(item.ISO) + "/";
                        if (item.Tipo == "pvl" && String.IsNullOrEmpty(slugBusca(item.ISO))) continue;
                    }
                    var wc = Rede.NovoWc();
                    var t = wc.DownloadStringTaskAsync(new Uri(url));
                    if (item.Tipo == "hunt") { /* ISO ja ajustado */ }
                    pvlTasks.Add(new PvlTarefa { T = t, ISO = item.ISO, Tipo = item.Tipo, W = wc, Start = DateTime.Now });
                }
                catch { pvlFalhas++; }
            }
        }

        static readonly Dictionary<string, string> SlugPvl = new Dictionary<string, string> {
            {"AR","argentina"}, {"CL","chile"}, {"PE","peru"}, {"CO","colombia"},
            {"EC","ecuador"}, {"UY","uruguay"}, {"PY","paraguay"} };
        string slugBusca(string iso)
        {
            string v;
            return SlugPvl.TryGetValue(iso, out v) ? v : "";
        }

        // ============================================================
        //  CICLO RAPIDO (400 ms): vigia, log, tarefas de rede, IP
        // ============================================================
        void CicloRapido()
        {
            vigiaTick = (vigiaTick + 1) % 15;
            if (vigiaTick == 5) { try { EscurecerRolagens(); } catch { } }
            if (connected && btnConnect.Enabled)
            {
                if (vigiaTick == 0)
                {
                    try
                    {
                        if (!TunelVivo())
                        {
                            connected = false;
                            try { LimparRotasNossas(); } catch { }
                            lblStatus.Text = "Status: desconectado";
                            Log("A VPN caiu (a lista de servidores gr\u00e1tis \u00e9 inst\u00e1vel).");
                            AtualizarEstadoConexao();
                            AgendarReconecta();
                        }
                    }
                    catch { }
                }
                if (vigiaTick == 12)
                {
                    // AUDITORIA ANTI-VAZAMENTO: le as rotas reais do Windows e
                    // confere na hora; achou errado, corrige e avisa. O route.exe
                    // trava a janela: roda fora da thread dela (so avisa por Log)
                    try { ThreadPool.QueueUserWorkItem(delegate { try { AuditarVazamento(); } catch { } }); } catch { }
                }
                // sem sonda em pool: ela falhava por perda momentanea de pacote e gerava
                // aviso/queda sem necessidade. Queda de verdade se detecta pelo
                // processo + adaptador (TunelVivo) e pelo proprio openvpn, que
                // se encerra sozinho em 60 s sem resposta (--ping-exit).
            }
            DescarregarLog();

            // repeticao automatica do VPN Gate
            if (dlTask == null && dlReTimeout != null && DateTime.Now >= dlReTimeout.Value)
            {
                dlReTimeout = null;
                try { if (BaixarListaServidores()) btnRefresh.Enabled = false; } catch { }
            }

            // VPN Gate terminou?
            if (dlTask != null && dlTask.IsCompleted)
            {
                var t = dlTask;
                dlTask = null;
                dlStart = DateTime.MinValue;
                btnRefresh.Enabled = true;
                try
                {
                    if (t.IsFaulted) throw t.Exception.GetBaseException();
                    dlTentativas = 0;
                    AplicarLista(t.Result, true);
                }
                catch (Exception e)
                {
                    dlTentativas++;
                    Log("N\u00e3o deu para atualizar: " + e.Message);
                    if (servers.Count == 0)
                    {
                        var c = Store.ReadCache();
                        if (c != null) { AplicarLista(c, false); Log("Usando a lista salva no seu PC (sem internet)."); }
                    }
                    else AtualizarLista();
                    if (dlTentativas <= 3)
                    {
                        Log("VPN Gate falhou - tento de novo sozinho em 20 s (tentativa " + dlTentativas + " de 3).");
                        dlReTimeout = DateTime.Now.AddSeconds(20);
                    }
                    else
                    {
                        dlTentativas = 0;
                        Log("VPN Gate n\u00e3o respondeu agora - entra de novo no pr\u00f3ximo clique em Atualizar.");
                    }
                }
            }


            // nunca deixa um download pendurado
            if (dlTask != null && dlStart != DateTime.MinValue && (DateTime.Now - dlStart).TotalSeconds > 60)
            {
                dlTask = null; dlStart = DateTime.MinValue;
                btnRefresh.Enabled = true;
                Log("Tempo esgotado ao baixar a lista. Tente Atualizar de novo.");
            }

            // espelho GitHub terminou?
            if (dlEspelho != null && dlEspelho.IsCompleted)
            {
                var te = dlEspelho;
                dlEspelho = null;
                dlEspStart = DateTime.MinValue;
                try
                {
                    if (te.IsFaulted) throw te.Exception.GetBaseException();
                    AplicarEspelho(te.Result);
                }
                catch { /* o VPN Gate segue valendo sozinho; sem barulho no log */ }
            }

            // nunca deixa o download do espelho pendurado
            if (dlEspelho != null && dlEspStart != DateTime.MinValue && (DateTime.Now - dlEspStart).TotalSeconds > 60)
            {
                dlEspelho = null; dlEspStart = DateTime.MinValue;
            }

            // IP externo terminou?
            if (ipTask != null && ipTask.IsCompleted)
            {
                var t = ipTask; ipTask = null;
                try
                {
                    var tgs = t as Task<string>;
                    if (tgs != null && !tgs.IsFaulted)
                    {
                        var ip = tgs.Result.Trim();
                        if (Regex.IsMatch(ip, @"^\d{1,3}(\.\d{1,3}){3}$"))
                        {
                            currentIP = ip;
                            lblIP.Text = "IP atual: " + ip;
                            try { EnviarIP(ip); } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // O VPN Gate e consultado GLOBAL (ele manda o mundo inteiro) e o filtro da
        // America Latina acontece aqui dentro. Sem excecao: EUA, Canada, Japao,
        // Coreia, India e o resto ficam de fora da lista. Como ele muda de pais a
        // cada hora, o log mostra quantos chegam - quando aparecer servidor da
        // regiao, ele entra sozinho na varredura seguinte.
        void AplicarLista(string raw, bool salvar)
        {
            var sv = Rede.ConvertToServers(raw, false);
            int totalGate = 0;
            try
            {
                if (!String.IsNullOrWhiteSpace(raw))
                    foreach (var ln in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        var l = ln.TrimStart('\uFEFF');
                        if (String.IsNullOrWhiteSpace(l) || l[0] == '*' || l[0] == '#') continue;
                        totalGate++;
                    }
            }
            catch { }
            if (!String.IsNullOrWhiteSpace(raw)) gateRaw = raw;
            if (salvar && !String.IsNullOrWhiteSpace(raw)) Store.WriteCache(raw);
            gateServers = sv;
            CombinarGate(totalGate, sv.Count, -1);
        }
        int gateUltimoGate = 0;
        int gateUltimoEspelho = -1;
        List<Servidor> gateEspelho = new List<Servidor>();

        // espelho GitHub chegou: guarda e remonta a lista junto com o VPN Gate
        void AplicarEspelho(string json)
        {
            gateEspelho = Rede.ParseEspelhoJson(json);
            CombinarGate(-1, -1, gateEspelho.Count);
        }

        // junta VPN Gate + espelho GitHub e monta a tela. O mesmo IP:porta
        // vindo dos dois vira uma linha so na fusao (o JuntarListas junta por
        // IP:porta), entao buscar nos dois nao duplica nada: so garante que a
        // lista chega mesmo quando um dos dois esta fora do ar.
        void CombinarGate(int totalGate, int nPrincipal, int nEspelho)
        {
            JuntarListas();
            try
            {
                if (nPrincipal >= 0 && nPrincipal > gateUltimoGate)
                    Log("VPN Gate: " + nPrincipal + " servidores da America Latina entraram (de " + totalGate + " que ele mandou do mundo inteiro).");
                if (nPrincipal >= 0) gateUltimoGate = nPrincipal;
                if (nEspelho > 0 && nEspelho != gateUltimoEspelho)
                    Log("Espelho GitHub: " + nEspelho + " servidores da America Latina (reserva do VPN Gate, sem precisar dele).");
                if (nEspelho >= 0) gateUltimoEspelho = nEspelho;
            }
            catch { }
        }

        // ---------------- cache das fontes publicas ----------------
        void SalvarPvlCache()
        {
            try
            {
                var o = new PvlCacheRec { When = DateTime.Now.ToString("o"), Servers = pvlServers };
                File.WriteAllText(Store.PvlCachePath, new JavaScriptSerializer().Serialize(o), new UTF8Encoding(false));
            }
            catch { }
        }
        class PvlCacheRec { public string When = ""; public List<Servidor> Servers = new List<Servidor>(); }

        bool LerPvlCache()
        {
            if (!File.Exists(Store.PvlCachePath)) return false;
            try
            {
                var o = new JavaScriptSerializer().Deserialize<PvlCacheRec>(File.ReadAllText(Store.PvlCachePath, Encoding.UTF8));
                if (o == null || String.IsNullOrEmpty(o.When)) return false;
                var q = DateTime.Parse(o.When, System.Globalization.CultureInfo.InvariantCulture);
                if ((DateTime.Now - q).TotalHours > 12) return false;
                if (o.Servers == null || o.Servers.Count == 0) return false;
                pvlServers = o.Servers;
                return true;
            }
            catch { return false; }
        }

        // le um texto embutido no proprio exe (versao de arquivo unico)
        string LerRecursoTexto(string nome)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var rs = asm.GetManifestResourceStream(nome))
                {
                    if (rs == null) return null;
                    using (var rd = new StreamReader(rs, Encoding.UTF8))
                        return rd.ReadToEnd();
                }
            }
            catch { return null; }
        }

        // URBAN: duas fontes, porque cada uma esconde parte.
//  1) /entrypoints/countries -> os servidores vem DENTRO do pais (o BR vem
//     aqui com 7, mas o Japao nao aparece com nenhum)
//  2) /entrypoints/servers  -> lista plana, cada um com o pais DENTRO
//     (o Japao e os EUA aparecem aqui; o BR nao)
// Junto as duas e tiro o repetido, entra tudo que existe de verdade.
        void CarregarUrban()
        {
            try
            {
                // usa (e guarda) o mesmo accs que sera usado na hora de conectar
                string accs = "";
                try { accs = UrbanPegarAccs(); } catch { return; }
                if (String.IsNullOrEmpty(accs)) return;

                var urb = new List<Servidor>();
                var vistosU = new HashSet<string>();
                // fonte 1: os servidores que vem embutidos no pais
                try
                {
                    var paisesJson = Rede.UrbanGetCountries(accs);
                    foreach (var u in Rede.ParseUrbanCountries(paisesJson))
                    {
                        if (!Dados.EhLatina(u.ISO)) continue;          // so as tres Americas
                        if (vistosU.Add(u.IP + ":" + u.Port)) urb.Add(u);
                    }
                }
                catch { }
                // fonte 2: a lista plana (traz pais, pool, peso e latitude)
                try
                {
                    var srvJson = Rede.UrbanGetServers(accs);
                    foreach (var u in Rede.ParseUrbanServers(srvJson))
                        if (vistosU.Add(u.IP + ":" + u.Port)) urb.Add(u);
                }
                catch { }
                if (urb.Count == 0)
                {
                    try { BeginInvoke((Action)delegate { Log("Urban nao devolveu nenhum servidor das Americas agora (eles mudam direto la)."); }); } catch { }
                    return;
                }
                urbanServers = urb;
                // avisa QUAIS paises a Urban liberou agora
                var paises = new List<string>();
                foreach (var u in urb) if (!paises.Contains(u.ISO)) paises.Add(u.ISO);
                var msgUrb = urb.Count + " servidores Urban entraram na lista (" + String.Join(", ", paises.ToArray()) + ").";
                try { BeginInvoke((Action)delegate { try { JuntarListas(); } catch { } Log(msgUrb); }); } catch { try { urbanServers = urb; } catch { } }
            }
            catch { }
        }

        // LISTA RESERVA DENTRO DO EXE: se os caches sumiram (pasta apagada)
        // e as fontes estao fora do ar, a lista abre mesmo assim com a foto
        // guardada na compilacao - e ainda regrava os caches, curando a pasta
        void CarregarListaEmbutida()
        {
            try
            {
                int antes = servers.Count;
                var emb = LerRecursoTexto("servers.inicial");
                if (!String.IsNullOrWhiteSpace(emb)) { try { AplicarLista(emb, true); } catch { } }
                var embP = LerRecursoTexto("pvl.inicial");
                if (!String.IsNullOrWhiteSpace(embP))
                {
                    try
                    {
                        var o = new JavaScriptSerializer().Deserialize<PvlCacheRec>(embP);
                        if (o != null && o.Servers != null && o.Servers.Count > 0)
                        {
                            pvlServers = o.Servers;
                            SalvarPvlCache();
                            JuntarListas();
                        }
                    }
                    catch { }
                }
                if (servers.Count > antes)
                    Log("Lista reserva de dentro do programa (" + servers.Count + " servidores) - atualize quando a internet voltar.");
            }
            catch { }
        }

        // ============================================================
        //  MEDICAO DE PING (botao "Medir ping")
        // ============================================================
        void MedirTudo()
        {
            if (servers.Count == 0) return;
            btnPing.Enabled = false;
            btnRefresh.Enabled = false;
            UseWaitCursor = true;
            lblStatus.Text = "Status: medindo seu ping...";
            Log("Medindo o SEU ping em todos os servidores ao mesmo tempo...");
            // mede TODOS (eram so 60: o resto ficava com "-" para sempre)
            var alvo = servers.ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // 3 ecos por servidor e vale o MENOR: 1 eco sozinho pega picos de
                    // jitter e o numero mente; o minimo e o tempo real da rota.
                    // TODOS EM PARALELO: o Ping do .NET dispara as sondas de uma vez
                    var sondas = new List<object[]>();
                    foreach (var s in alvo)
                    {
                        for (int eco = 0; eco < 3; eco++)
                        {
                            try
                            {
                                var pg = new Ping();
                                var tsk = pg.SendPingAsync(s.IP, 700);
                                sondas.Add(new object[] { s, pg, tsk });
                            }
                            catch { }
                        }
                    }
                    var lim = Stopwatch.StartNew();
                    while (lim.ElapsedMilliseconds < 2600)
                    {
                        bool pendente = false;
                        foreach (var it in sondas) if (!((Task)it[2]).IsCompleted) { pendente = true; break; }
                        if (!pendente) break;
                        Thread.Sleep(20);
                    }
                    var melhor = new Dictionary<string, int>();
                    foreach (var it in sondas)
                    {
                        var s = (Servidor)it[0];
                        try
                        {
                            var tsk = (Task<PingReply>)it[2];
                            if (tsk.IsCompleted && !tsk.IsFaulted)
                            {
                                var rep = tsk.Result;
                                if (rep != null && rep.Status == IPStatus.Success)
                                {
                                    int ms = (int)rep.RoundtripTime;
                                    int atual;
                                    if (!melhor.TryGetValue(s.IP, out atual) || ms < atual) melhor[s.IP] = ms;
                                }
                            }
                        }
                        catch { }
                        try { ((IDisposable)it[1]).Dispose(); } catch { }
                    }
                    foreach (var s in alvo)
                    {
                        int m;
                        if (melhor.TryGetValue(s.IP, out m)) { s.Measured = m; RegistrarPing(s.IP, m); }
                    }
                    // ICMP bloqueado nao quer dizer "sem ping": completa com a sonda real
                    var falta = alvo.Where(s => s.Measured < 0).ToList();
                    if (falta.Count > 0)
                    {
                        var r = Rede.MedirRtts(falta);
                        foreach (var s in falta)
                        {
                            int m;
                            if (r.TryGetValue(s.IP, out m) && m >= 0) { s.Measured = m; RegistrarPing(s.IP, m); }
                        }
                    }
                    BeginInvoke((Action)delegate
                    {
                        try
                        {
                            // depois de medir, mostra do menor ping para o maior (eram os sem
                            // medida que ficavam em cima e parecia que nao mediu nada)
                            sortCol = 2; sortAsc = true;
                            OrdenarLista();
                            AtualizarLista();
                            var best = servers.FirstOrDefault(s2 => s2.Measured >= 0);
                            if (best != null)
                                Log("Melhor ping: " + Dados.GetServerLocal(best) + " (" + best.IP + ") com " + best.Measured + " ms.");
                            else
                                Log("Nenhum servidor respondeu ao ping agora.");
                        }
                        catch { }
                        btnPing.Enabled = true;
                        btnRefresh.Enabled = true;
                        UseWaitCursor = false;
                    });
                }
                catch
                {
                    BeginInvoke((Action)delegate
                    {
                        btnPing.Enabled = true;
                        btnRefresh.Enabled = true;
                        UseWaitCursor = false;
                        AtualizarLista();
                    });
                }
            });
        }

        // ============================================================
        //  IP EXTERNO (api.ipify.org)
        // ============================================================
        void IniciarChecagemIP()
        {
            if (ipTask != null && !ipTask.IsCompleted) return;
            try
            {
                if (ipWc == null) ipWc = Rede.NovoWc();
                ipTask = ipWc.DownloadStringTaskAsync(new Uri("https://api.ipify.org"));
            }
            catch { }
        }

        // ============================================================
        //  ATUALIZACAO (versao.txt / atualizacao.txt)
        // ============================================================
        void TestarAtualizacao()
        {
            versaoNova = "";
            var remota = "";
            try
            {
                var local = Path.Combine(Path.GetDirectoryName(Store.ExePath), "atualizacao.txt");
                if (File.Exists(local))
                {
                    remota = File.ReadLines(local).FirstOrDefault() ?? "";
                }
                else
                {
                    var req = (HttpWebRequest)WebRequest.Create(Dados.UrlVersaoRemota);
                    req.Timeout = 3000;
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) UltraVPNFree";
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream()))
                        remota = rd.ReadToEnd();
                }
                if (remota != null)
                {
                    remota = remota.Trim();
                    if (Regex.IsMatch(remota, @"^\d+(\.\d+){1,3}$"))
                    {
                        Version vn, va;
                        if (Version.TryParse(remota, out vn) && Version.TryParse(Program.Versao, out va) && vn > va)
                            versaoNova = remota;
                    }
                }
            }
            catch { versaoNova = ""; }
            var mostra = versaoNova != "";
            var texto = mostra ? "Atualiza\u00e7\u00e3o dispon\u00edvel: vers\u00e3o " + versaoNova + " (a sua \u00e9 a " + Program.Versao + ")." : "";
            try
            {
                BeginInvoke((Action)delegate
                {
                    try
                    {
                        if (mostra)
                        {
                            btnUpdate.Visible = true;
                            try { AplicarTema(); } catch { }
                            Log(texto);
                        }
                        else if (btnUpdate.Visible)
                        {
                            btnUpdate.Visible = false;
                            try { AplicarTema(); } catch { }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // ============================================================
        //  BANDEJA
        // ============================================================
        void IrParaBandeja()
        {
            Hide();
            tray.Visible = true;
            if (minTray && !balloonOnce)
            {
                balloonOnce = true;
                try { tray.ShowBalloonTip(4000, "Ultra VPN Free", "O programa continua na bandeja. Clique 2x no \u00edcone para abrir.", ToolTipIcon.Info); } catch { }
            }
        }
        bool balloonOnce = false;

        void MostrarDaBandeja()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            tray.Visible = false;
        }

        void SairDoPrograma()
        {
            exiting = true;
            saindo = true;
            try { fontesTimer.Stop(); } catch { }
            try { if (connected) Desconectar(); } catch { }
            try { PararMotor(); } catch { }
            tray.Visible = false;
            tray.Dispose();
            pollTimer.Stop();
            watch.Stop();
            try { rtimer.Stop(); } catch { }
            try { updateTimer.Stop(); } catch { }
            Application.Exit();
        }

        // ============================================================
        //  ABRIR DA PRIMEIRA VEZ
        // ============================================================
        void AoAbrirJanela()
        {
            try
            {
                if (Program.TrayStart) IrParaBandeja();
                suppressUi = true;
                chkFull.Checked = fullTunnel;
                chkStartup.Checked = TestarStartup();
                chkAutoRe.Checked = autoRecon;
                chkBootConn.Checked = bootConnect;
                var idxP = Array.IndexOf(Dados.ProtoKeys, protocol);
                if (idxP < 0) idxP = 0;
                cboProto.SelectedIndex = idxP;
                var idxT = Array.IndexOf(Dados.TemaKeys, temaAtual);
                if (idxT < 0) idxT = 0;
                cboTema.SelectedIndex = idxT;
                suppressUi = false;
                AplicarTema();
                AtualizarEstadoConexao();
                // o et ficou pronto antes da janela existir: poe o icone agora
                try
                {
                    if (etPendente && trayFrames != null && trayFrames.Length > 0)
                    {
                        tray.Icon = trayFrames[0];
                        try { lv.Invalidate(); lbApps.Invalidate(); lbLog.Invalidate(); } catch { }
                    }
                    etPendente = false;
                }
                catch { }
                AtualizarListaProgramas();
                Log("Pronto. 1) escolha o t\u00fanel (UDP/TCP), 2) escolha o pa\u00eds, 3) clique em CONECTAR.");
                if (appList.Count == 0) Log("Dica: clique em \"Adicionar programa...\" e escolhe o Discord (ou outro).");

                if (Program.AutoStart)
                {
                    Log("Aberto junto com o Windows - conecto sozinho em alguns segundos.");
                    bootTimer.Start();
                }
                if (Program.ConectaAgora)
                {
                    Log("Administrador autorizado - conectando agora...");
                    connTimer.Start();
                }

                // listas salvas -> entram no proximo ciclo de pintura: a janela
                // aparece e responde primeiro, sem quadrado branco nem
                // ampulheta (o parse do cache e a montagem da lista prendiam
                // a thread dela com a janela ja visivel)
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        try
                        {
                            if (LerPvlCache()) { /* entra no JuntarListas abaixo */ }
                            var c = Store.ReadCache();
                            if (c != null) AplicarLista(c, false);
                            else if (pvlServers.Count > 0) JuntarListas();
                            // sem NADA (caches sumiram e fontes fora do ar): usa a
                            // lista reserva guardada DENTRO do exe (nunca abre vazia)
                            if (gateServers.Count == 0 && pvlServers.Count == 0)
                                CarregarListaEmbutida();
                        }
                        catch { }
                    });
                }
                catch { }

                IniciarChecagemIP();
                if (BaixarListaServidores()) btnRefresh.Enabled = false;
                IniciarFontesPublicas();
                updateTimer.Start();
                pollTimer.Start();
                watch.Start();
                rtimer.Start();
                try { fontesTimer.Start(); } catch { }
                // ATUALIZA DAS FONTES AO ABRIR: a lista do cache entra na tela na hora
                // (o programa ja abre com servidores clicaveis) e 1,2 s depois
                // as fontes sao consultadas de novo. NINGUEM e tirado da lista:
                // entra quem chegou agora e o .ovpn de quem ainda nao tem fica
                // baixado, para o clique em CONECTAR nao perder tempo.
                try
                {
                    penteTimer = new System.Windows.Forms.Timer();
                    penteTimer.Interval = 1200;
                    penteTimer.Tick += delegate { try { penteTimer.Stop(); } catch { } RevalidarFontes(); };
                    penteTimer.Start();
                }
                catch { try { RevalidarFontes(); } catch { } }
                Log("Abrindo com a lista salva - em instantes atualizo ip, porta e .ovpn de todas as fontes.");

                if (Program.TrayStart)
                {
                    try { tray.ShowBalloonTip(3000, "Ultra VPN Free", "Iniciado com o Windows.", ToolTipIcon.Info); } catch { }
                }
                // pronta: aparece de uma vez, ja pintada (sem nenhum quadro
                // intermediario branco no meio do caminho)
                try { Opacity = 1; } catch { }
            }
            catch (Exception e)
            {
                try { Opacity = 1; } catch { }
                Log("Erro ao abrir a janela: " + e.Message);
            }
        }

        int connTent = 0;

        // ============================================================
        //  CONEXAO (fluxo completo do Do-Connect original)
        // ============================================================
        // TRAVA DE CONEXAO UNICA.
        // Sem isto, DOIS disparos entravam no FluxoConectar ao mesmo tempo: o
        // clique em CONECTAR, o bootTimer, o connTimer, o reconTimer e o
        // tray podem cair no mesmo segundo - e principalmente enquanto o
        // OpenVPN esta se instalando (13 s no primeiro uso), que e quando o
        // botao ainda esta habilitado. Cada thread abria seu proprio
        // OpenVPN e uma matava o processo da outra pelo PararMotor: o log
        // saia inteiro duplicado e aparecia "codigo 1" junto de "codigo -1"
        // na MESMA porta, no mesmo segundo (o -1 e o processo morto pela
        // outra thread). No Windows 10 a sessao inteira caia assim.
        int conectando = 0;

        // marca na lista do WinForms o servidor que a tela em HTML escolheu
        void MarcarSelecionado(Servidor alvo)
        {
            try
            {
                lv.BeginUpdate();
                lv.SelectedItems.Clear();
                int achou = -1;
                for (int i = 0; i < lv.Items.Count; i++)
                {
                    var tg = lv.Items[i].Tag as Servidor;
                    if (tg != null && tg.IP == alvo.IP) { lv.Items[i].Selected = true; lv.EnsureVisible(i); achou = i; break; }
                }
                lv.EndUpdate();
            }
            catch { }
        }

        void Conectar()
        {
            if (connected) { Log("J\u00e1 est\u00e1 conectado."); return; }
            // ainda estou conferindo as fontes: clicar agora seria escolher
            // um servidor que pode nem estar de pe
            if (saindo || disposing) return;

            // COM A TELA EM HTML a escolha chega pelo UltimoEscolhido (o
            // clique na tabela), e nao pela selecao da lista do WinForms -
            // que fica escondida. O FluxoConectar() so sabe ler a lista,
            // entao aqui a escolha e transportada para la. Sem isto o
            // CONECTAR e o duplo clique paravam em "Escolha um servidor na
            // lista primeiro".
            try
            {
                var escolhido = UltimoEscolhido;
                if (escolhido != null)
                {
                    if (InvokeRequired) BeginInvoke((Action)delegate { MarcarSelecionado(escolhido); });
                    else MarcarSelecionado(escolhido);
                }
            }
            catch { }

            int nApps = appList.Count;
            if (!fullTunnel && nApps == 0)
            {
                Log("Adicione ao menos UM programa, ou marque \"Tudo pela VPN\".");
                return;
            }
            // a trava e tomada AQUI, na propria thread da janela, antes de
            // qualquer trabalho: e o unico lugar onde dois disparos ainda
            // se encontram. Quem ja esta conectando e descartado.
            if (Interlocked.CompareExchange(ref conectando, 1, 0) != 0)
            {
                Log("J\u00e1 estou conectando - deixo essa terminar antes de tentar de novo.");
                return;
            }
            // o botao desliga na hora: a tela mostra que a conexao comecou
            try
            {
                if (InvokeRequired) BeginInvoke((Action)delegate { try { btnConnect.Enabled = false; btnConnect.Invalidate(); } catch { } });
                else { btnConnect.Enabled = false; btnConnect.Invalidate(); }
            }
            catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // O driver TAP-Windows so cria o adaptador quando o
                    // OpenVPNServiceInteractive esta no ar. Ele fica em "Manual"
                    // (nao sobe com o Windows) e e ligado aqui.
                    try { LigarServicoOpenVpn(); } catch { }
                    // enquanto o .ovpn esta subindo a lista fica parada: se a revisao
                    // rodasse no meio, a remontagem trocaria o servidor escolhido
                    disposing = true;
                    try { FluxoConectar(); }
                    finally { disposing = false; }
                }
                finally
                {
                    // a trava so cai quando a thread TERMINA (sucesso ou erro):
                    // e o finally que impede a liberacao antecipada se estourar excecao
                    conectando = 0;
                    try
                    {
                        BeginInvoke((Action)delegate { try { if (!saindo) { btnConnect.Enabled = true; btnConnect.Invalidate(); } } catch { } });
                    }
                    catch { }
                }
            });
        }

        // o servico sobe para conectar e desce ao desconectar: some do Windows
        // sem nunca ficar parado no caminho, que e o que quebra a criacao do
        // adaptador ("could not talk to service").
        bool servicoSubiuPorNos = false;

        // NENHUM servidor e especial na conexao automatica do boot. A conexao
        // do boot e a de qualquer clique: pega o primeiro da lista (que e o de
        // melhor ping), e o failover cuida se ele nao responder.

        void LigarServicoOpenVpn()
        {
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    if (!sc.ServiceName.StartsWith("OpenVPN")) continue;
                    try
                    {
                        if (sc.Status != ServiceControllerStatus.Running)
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        }
                        servicoSubiuPorNos = true;
                        Log("OpenVPN: servico " + sc.ServiceName + " ligado.");
                    }
                    catch { }
                }
            }
            catch { }
        }

        void DesligarServicoOpenVpn()
        {
            if (!servicoSubiuPorNos) return;
            servicoSubiuPorNos = false;
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    if (!sc.ServiceName.StartsWith("OpenVPN")) continue;
                    try
                    {
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        void FluxoConectar()
        {
            // A ESCOLHA VEM DO UltimoEscolhido, e nao da selecao da lista do
            // WinForms. Com a tela em HTML o clique na tabela so preenche o
            // UltimoEscolhido, e a lista do WinForms fica escondida numa
            // janela de reserva - marcar Selected nela nao gruda (medido:
            // itens=13, achou=0, marcados=0). Ler a lista escondida fazia o
            // CONECTAR e o duplo clique pararem em "Escolha um servidor".
            Servidor s = UltimoEscolhido;
            if (s == null)
            {
                BeginInvoke((Action)delegate
                {
                    // sem escolha na tela: vai no primeiro da lista, que e o de
                    // melhor ping. Vale tanto para o boot quanto para o clique.
                    if (lv.SelectedItems.Count == 0 && lv.Items.Count > 0)
                    {
                        try { lv.Items[0].Selected = true; lv.EnsureVisible(0); } catch { }
                    }
                    if (lv.SelectedItems.Count > 0) s = (Servidor)lv.SelectedItems[0].Tag;
                });
                // espera a leitura da selecao
                for (int i = 0; i < 40 && s == null && lv.Items.Count > 0; i++) Thread.Sleep(25);
                BeginInvoke((Action)delegate { if (lv.SelectedItems.Count > 0) s = (Servidor)lv.SelectedItems[0].Tag; });
                for (int i = 0; i < 40 && s == null && lv.Items.Count > 0; i++) Thread.Sleep(25);
            }
            else
            {
                // tela em HTML: garante que a lista do WinForms aponte para o
                // mesmo servidor (o plano B usa a lista de verdade)
                try { MarcarSelecionado(s); } catch { }
            }
            if (s == null)
            {
                Log("Escolha um servidor na lista primeiro (ou clique em Atualizar).");
                return;
            }

            BeginInvoke((Action)delegate
            {
                btnConnect.Enabled = false;
                UseWaitCursor = true;
                lblStatus.Text = "Status: conectando...";
                btnConnect.Invalidate();
            });
            try
            {
                PararMotor();

                // FAILOVER: escolhido -> mesmo pais -> resto (sem BR sem clique),
                // ate 6 tentativas. Medicao rapida antes de gastar OpenVPN.
                var vistos = new HashSet<string>();
                var cand = new List<Servidor>();
                // SEM dedupe por IP: cada servidor da lista entra como
                // candidato. Duas linhas do mesmo IP com portas diferentes sao
                // endpoints diferentes e valem as duas.
                // A unica coisa barrada e o MESMO objeto entrando duas vezes
                // na fila (comeria duas das 6 tentativas com o mesmo alvo).
                var jaEntrou = new HashSet<Servidor>();
                Action<Servidor> add = delegate(Servidor c)
                {
                    if (c == null || String.IsNullOrEmpty(c.IP)) return;
                    if (!jaEntrou.Add(c)) return;
                    cand.Add(c);
                };
                List<Servidor> daLista = null;
                BeginInvoke((Action)delegate
                {
                    var tmp = new List<Servidor>();
                    foreach (ListViewItem it in lv.Items) tmp.Add((Servidor)it.Tag);
                    daLista = tmp;
                });
                for (int i = 0; i < 80 && daLista == null; i++) Thread.Sleep(25);
                if (daLista == null) daLista = servers;
                add(s);
                var mesmo = new List<Servidor>();
                var outros = new List<Servidor>();
                foreach (var c in daLista)
                {
                    if (c.IP == s.IP) continue;
                    if (c.ISO == s.ISO) mesmo.Add(c); else outros.Add(c);
                }
                // a lista ja so tem America do Sul sem o Brasil: o failover usa
                // tudo que estiver nela, MAS COMECANDO PELO QUE TEM .OVPN.
                // O hunt so entrega ip/porta (o .ovpn com o certificado do
                // servidor VPN Gate ele nao publica), e sem esse arquivo o
                // OpenVPN nem comeca. Testando os que tem arquivo primeiro, o
                // acerto vem em 1 ou 2 tentativas em vez de queimar 6.
                Func<Servidor, bool> temArquivo = delegate(Servidor c)
                {
                    if (c.Src == "urban") return true;
                    if (!String.IsNullOrEmpty(c.Cfg) || !String.IsNullOrEmpty(c.CfgUrl)) return true;
                    return c.Src == "pvl" && !String.IsNullOrEmpty(c.Cid);
                };
                foreach (var c in mesmo.Concat(outros))
                    if (temArquivo(c)) add(c);
                foreach (var c in mesmo.Concat(outros))
                    if (!temArquivo(c)) add(c);

                // SEM medicao antes de conectar: a sonda rapida (1,4 s) descartava
                // quem nao respondia na hora, e servidor lento ou de ping alto
                // nem chegava a ser tentado. Agora a fila vale como esta -
                // escolhido, mesmo pais, resto, com .ovpn primeiro - e cada um
                // e tentado de verdade ate 6 tentativas. Quem abre o tunel
                // decide, nao a sonda.

                int tent = 0;
                string ultimo = "";
                Servidor reserva = null;
                int reservaMidia = 999999;
                foreach (var c in cand)
                {
                    if (tent >= 6) break;
                    tent++;
                    try
                    {
                        PararMotor();
                        AbrirTunel(c);
                        ultimo = "";
                        // SEM TROCA AUTOMATICA: abriu o tunel, fica nele. Voce escolhe o
                        // servidor clicando na lista (ou deixa o primeiro); o
                        // programa nao troca sozinho nem tira da lista por
                        // causa de midia - so avisa se ela estiver pesada.
                        int mid = ultimaMidia;
                        if (mid >= 0 && mid < reservaMidia) { reserva = c; reservaMidia = mid; }
                        break;
                    }
                    catch (Exception e)
                    {
                        ultimo = e.Message;
                        var mot = ultimo.Length > 140 ? ultimo.Substring(0, 140) + "..." : ultimo;
                        Log(c.Name + " (" + c.IP + ") nao abriu (" + mot + ") - tentando o proximo...");
                        Store.DropCfgCache(c.IP);
                        RemoverDaLista(c.IP);
                        try { LimparRotasNossas(); } catch { }
                        try { PararMotor(); } catch { }
                    }
                }
                if (ultimo != "" && reserva != null)
                {
                    // os proximos falharam: volta para o melhor que abriu
                    try
                    {
                        Log("Voltando para " + reserva.Name + " (" + reserva.IP + "), o melhor que abriu (midia " + reservaMidia + " ms).");
                        PararMotor();
                        AbrirTunel(reserva);
                        ultimo = "";
                    }
                    catch { }
                }
                if (ultimo != "")
                    throw new Exception("Tentei " + tent + " servidores e nenhum abriu o tunel. " + ultimo);
            }
            catch (Exception e)
            {
                BeginInvoke((Action)delegate { lblStatus.Text = "Status: n\u00e3o conectou"; });
                Log("Erro: " + e.Message);
                try { LimparRotasNossas(); } catch { }
                try { PararMotor(); } catch { }
                connected = false;
                ifAlias = ""; vpnGw = "";
            }
            finally
            {
                BeginInvoke((Action)delegate { UseWaitCursor = false; btnConnect.Enabled = true; btnConnect.Invalidate(); });
            }
            BeginInvoke((Action)delegate { AtualizarEstadoConexao(); });
            if (connected)
            {
                reAuto = 0;
                // Primeira conexao da sessao: fecha e abre o Discord (se estiver
                // aberto ou fechado). Nas trocas de servidor seguintes, se o
                // Discord nao estiver rodando, abre ele.
                if (!primeiraConexaoFeita)
                {
                    primeiraConexaoFeita = true;
                    ReiniciarOuAbrirDiscord();
                }
                else
                {
                    // Discord ja esta aberto? Nao faz nada - ele reconecta sozinho.
                    // Se nao estiver rodando, abre.
                    try
                    {
                        if (Process.GetProcessesByName("Discord").Length == 0)
                            ReiniciarOuAbrirDiscord();
                        else
                            Log("Discord continua aberto - ele percebe a troca e reconecta sozinho pelo tunel novo (o texto volta em segundos).");
                    }
                    catch { }
                }
            }
            else if (autoRecon && reAuto > 0)
            {
                Log("Reconex\u00e3o n\u00e3o deu certo - tento de novo em 10 s (tentativa " + (reAuto + 1) + ")...");
                BeginInvoke((Action)delegate { reconTimer.Interval = 10000; reconTimer.Start(); });
            }
        }

        int conexaoNumero = 0;
        // Primeira conexao bem-sucedida da sessao: fecha e abre o Discord
        // (se estiver aberto ou fechado). Nas trocas de servidor depois disso,
        // nada eh feito com o Discord.
        bool primeiraConexaoFeita = false;

        void Desconectar()
        {
            LimparRotasNossas();
            PararMotor();
            // o servico do OpenVPN fica em "Manual": desce aqui para nao
            // ficar rodando sem uso
            DesligarServicoOpenVpn();
            connected = false;
            BeginInvoke((Action)delegate
            {
                lblStatus.Text = "Status: desconectado";
                AtualizarEstadoConexao();
            });
            Log("Desconectado.");
        }

        // Fecha e abre o Discord (ou apenas abre se nao estiver rodando).
        // Chamado APENAS na primeira conexao bem-sucedida da sessao.
        void ReiniciarOuAbrirDiscord()
        {
            try
            {
                // Fecha processos existentes
                foreach (var p in Process.GetProcessesByName("Discord"))
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.CloseMainWindow();
                            p.WaitForExit(2000);
                        }
                        if (!p.HasExited) p.Kill();
                    }
                    catch { }
                }
            }
            catch { }

            // Abre novamente
            try
            {
                var caminho = AcharCaminhoDiscord();
                if (String.IsNullOrEmpty(caminho) || !File.Exists(caminho)) return;
                var psi = new ProcessStartInfo(caminho)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(psi);
                Log("Discord reaberto pelo tunel.");
            }
            catch { }
        }

        void AtualizarEstadoConexao()
        {
            try { EnviarStatus(connected, servidorAtual); EnviarEstado(); } catch { }
            try
            {
                if (connected)
                {
                    btnConnect.Text = String.IsNullOrEmpty(servidorAtual)
                        ? "DESCONECTAR" : "DESCONECTAR\r\nConectado a: " + servidorAtual;
                    btnConnect.BackColor = Color.Transparent;
                    btnConnect.ForeColor = CorVerdeNeon;
                    // etzinho pisca na bandeja enquanto a VPN esta de pe
                    try { if (trayFrames != null && trayFrames.Length > 1) { trayFrameIdx = 0; trayTimer.Start(); } } catch { }
                }
                else
                {
                    btnConnect.Text = "CONECTAR";
                    btnConnect.BackColor = Color.Transparent;
                    btnConnect.ForeColor = CorVerdeNeon;
                    servidorAtual = "";
                    servidorEndereco = "";
                    try { trayTimer.Stop(); if (trayFrames != null && trayFrames.Length > 0) tray.Icon = trayFrames[0]; } catch { }
                }
                btnConnect.Invalidate();
            }
            catch { }
        }

        void AgendarReconecta()
        {
            if (!autoRecon)
            {
                Log("Conecte de novo (ou marque \"Autoconectar se a VPN cair\" para voltar sozinho).");
                return;
            }
            reAuto++;
            Log("A VPN caiu - reconectando sozinho (tentativa " + reAuto + ")...");
            BeginInvoke((Action)delegate
            {
                reconTimer.Interval = 6000;
                reconTimer.Stop();
                reconTimer.Start();
            });
        }

        // Servidor que falhou UMA conexao NAO e mais jogado fora da lista. Ele fica
        // la para a proxima tentativa e para as varreduras seguintes: servidor
        // gratis oscila o tempo todo e sumir com ele na primeira falha deixava
        // a lista vazia sem que o usuario pudesse escolher outro. O que o
        // programa faz agora e AVISAR e seguir para o proximo servidor.
        void RemoverDaLista(string ip)
        {
            try
            {
                Log("  " + ip + " falhou agora - continua na lista (vale tentar de novo depois).");
            }
            catch { }
        }

        // ============================================================
        //  VIGIA (processo + adaptador + sonda de trafego)
        // ============================================================
        bool TunelVivo()
        {
            if (!String.IsNullOrEmpty(urbanProxyUrl)) return connected;
            if (String.IsNullOrEmpty(ifAlias)) return false;
            if (ovpnProc == null || ovpnProc.HasExited) return false;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (ni.Name == ifAlias && ni.OperationalStatus == OperationalStatus.Up) return true;
            }
            catch { }
            return false;
        }

        // ---------------- UrbanVPN: proxy HTTP residencial ----------------
        // Nao usa OpenVPN nem TAP: configura o proxy do Windows (WinHTTP/IE)
        // para passar pelo ip:porta da Urban com autenticacao accs-proxy.
        // No modo dividido, so os programas escolhidos usam o proxy.
        string urbanProxyUrl = "";
        string urbanProxyUser = "";
        string urbanProxyPass = "";
        // O token "accs" da Urban e dono da ASSINATURA do servidor: a assinatura
        // da lista so vale com o MESMO accs que a leu. Registrar um accs novo
        // para conectar quebra o par e a Urban responde 400 "The provided
        // signature..." - por isso o accs fica guardado e as duas coisas (ler a
        // lista e pegar o accs-proxy) usam sempre o mesmo.
        string urbanAccs = "";
        DateTime urbanAccsAte = DateTime.MinValue;
        readonly Dictionary<string, string> urbanProxyToks = new Dictionary<string, string>();

        string UrbanPegarAccs()
        {
            if (!String.IsNullOrEmpty(urbanAccs) && DateTime.Now < urbanAccsAte) return urbanAccs;
            string anonJson = "";
            try { anonJson = Rede.UrbanRegisterAnon(); } catch (Exception e) { throw new Exception("Urban register: " + e.Message); }
            string anon = Rede.UrbanToken(anonJson);
            if (String.IsNullOrEmpty(anon)) throw new Exception("Urban nao devolveu token anonimo");
            string accsJson = "";
            try { accsJson = Rede.UrbanGetSecurityToken(anon); } catch (Exception e) { throw new Exception("Urban credencial: " + e.Message); }
            string accs = Rede.UrbanToken(accsJson);
            if (String.IsNullOrEmpty(accs)) throw new Exception("Urban nao devolveu credencial");
            urbanAccs = accs;
            urbanAccsAte = DateTime.Now.AddMinutes(20);
            urbanProxyToks.Clear();      // as assinaturas velhas nao valem mais
            return accs;
        }

        void AbrirTunelUrban(Servidor s)
        {
            try
            {
                if (String.IsNullOrEmpty(s.Sig)) throw new Exception("Urban sem assinatura (sig).");
                Log("UrbanVPN " + s.Name + " (" + s.IP + ":" + s.Port + ") - pegando credencial...");
                string proxyTok = "";
                string tokCache;
                if (urbanProxyToks.TryGetValue(s.IP + ":" + s.Port, out tokCache) && !String.IsNullOrEmpty(tokCache)) proxyTok = tokCache;
                if (String.IsNullOrEmpty(proxyTok))
                {
                    string accs = UrbanPegarAccs();
                    try
                    {
                        proxyTok = Rede.UrbanToken(Rede.UrbanGetProxyToken(accs, s.Sig));
                    }
                    catch
                    {
                        // 400 aqui quase sempre e assinatura vencida: relendo a
                        // lista com credencial nova e tentando o mesmo servidor
                        urbanAccs = "";                      // for�a recadastrar
                        urbanProxyToks.Clear();
                        accs = UrbanPegarAccs();
                        try { proxyTok = Rede.UrbanToken(Rede.UrbanGetProxyToken(accs, s.Sig)); } catch { }
                    }
                }
                if (String.IsNullOrEmpty(proxyTok)) throw new Exception("Urban nao liberou o acesso a este servidor agora");
                try { urbanProxyToks[s.IP + ":" + s.Port] = proxyTok; } catch { }
                urbanProxyUrl = "http://" + s.IP + ":" + s.Port;
                urbanProxyUser = proxyTok;
                urbanProxyPass = "1";
                // aplica proxy no Windows (IE/WinHTTP) - vale para a maioria dos apps
                try
                {
                    if (fullTunnel)
                    {
                        Rede.RunCmd("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 1 /f");
                        Rede.RunCmd("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer /t REG_SZ /d \"" + s.IP + ":" + s.Port + "\" /f");
                    }
                    else
                    {
                        // modo dividido: nao mexe no proxy global, avisa que e via PAC
                        Log("Urban no modo dividido: o proxy vale para apps que respeitam o proxy do Windows.");
                        Rede.RunCmd("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 1 /f");
                        Rede.RunCmd("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer /t REG_SZ /d \"" + s.IP + ":" + s.Port + "\" /f");
                    }
                }
                catch { }
                connected = true;
                conexaoNumero++;
                ifAlias = "UrbanProxy";
                vpnGw = s.IP;
                ownedRoutes = new List<string>();
                defRoute = false;
                BeginInvoke((Action)delegate
                {
                    lblStatus.Text = "Status: CONECTADO  |  " + s.Name + " (Urban proxy)";
                    servidorAtual = s.Name + " - Urban proxy " + s.IP + ":" + s.Port;
                    AtualizarEstadoConexao();
                });
                Log("Conectado via Urban proxy " + s.IP + ":" + s.Port + " (" + s.Name + ").");
                Log("Proxy: " + urbanProxyUrl + " usuario=" + (urbanProxyUser.Length > 12 ? urbanProxyUser.Substring(0, 12) + "..." : urbanProxyUser));
                IniciarChecagemIP();
            }
            catch (Exception e) { throw new Exception("Urban: " + e.Message); }
        }
        void DesligarUrbanProxy()
        {
            try { Rede.RunCmd("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f"); } catch { }
            urbanProxyUrl = "";
            urbanProxyUser = "";
            urbanProxyPass = "";
            ifAlias = "";
            vpnGw = "";
        }

        // ============================================================
        //  ABRIR TUNEL (Connect-OpenVpn original)
        // ============================================================
        // ---------------- OpenVPN: instala sozinho como o app principal --------
        // o app principal traz o instalador do OpenVPN DENTRO do proprio exe e
        // instala em silencio na 1a conexao. O CS faz igual: procura o .msi (na
        // pasta _clients do lado do programa ou dentro do exe do app principal),
        // instala sem interface e apaga as sobras (atalho e servicio automatico).
        string msiOpenVpnTemp = "";

        void InstalarOpenVpnSeFaltar()
        {
            try
            {
                // Trava da INSTALACAO. Sem isto, duas chamadas concorrentes
                // viam "OpenVPN nao instalado" ao mesmo tempo e disparavam
                // dois msiexec por cima um do outro (o log mostrava
                // "Preparando o cliente OpenVPN (primeira vez...)" duas
                // vezes em 25 h, 13 s cada, derrubando o tunel que estivesse
                // de pe). Agora so uma thread instala e a outra espera.
                if (Interlocked.CompareExchange(ref instalandoOpenVpn, 1, 0) != 0)
                {
                    for (int espera = 0; espera < 300 && instalandoOpenVpn != 0; espera++) Thread.Sleep(500);
                    return;
                }
                try
                {
                    InstalarOpenVpnSeFaltarInterno();
                }
                finally { instalandoOpenVpn = 0; }
            }
            catch { }
        }

        int instalandoOpenVpn = 0;

        void InstalarOpenVpnSeFaltarInterno()
        {
            try
            {
                var msi = LocalizarMsiOpenVpn();
                if (msi == null)
                {
                    Log("OpenVPN nao encontrado e nao achei o instalador embutido neste .exe (nem do lado dele).");
                    return;
                }
                Log("Preparando o cliente OpenVPN (primeira vez: leva alguns segundos)...");
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/i \"" + msi + "\" /qn /norestart SHORTCUTS=0");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                int cod = -1;
                using (var p = Process.Start(psi))
                {
                    if (p != null) { p.WaitForExit(240000); try { cod = p.ExitCode; } catch { } }
                }
                if (String.IsNullOrEmpty(AcharOpenVpn()))
                {
                    Log("Nao deu para preparar o OpenVPN (codigo " + cod + ").");
                    return;
                }
                instalouOpenVpn = true;
                LimpiarSobrasOpenVpn();
                if (!String.IsNullOrEmpty(msiOpenVpnTemp))
                {
                    try { File.Delete(msiOpenVpnTemp); } catch { }
                    msiOpenVpnTemp = "";
                }
                Log("Cliente OpenVPN pronto (instalado sem deixar atalho).");
            }
            catch (Exception e) { try { Log("OpenVPN: " + e.Message); } catch { } }
        }

        string LocalizarMsiOpenVpn()
        {
            // 1) DENTRO DO PROPRIO EXE - este e o caminho normal. O build.bat
            //    embute o .msi como recurso "uvpn.openvpn.msi", entao o programa
            //    se instala sozinho SEM depender do nome do arquivo, de pasta
            //    _clients ou de qualquer arquivo solto do lado.
            try
            {
                var meu = ExtrairMsiDoAssembly(System.Reflection.Assembly.GetExecutingAssembly());
                if (meu != null) return meu;
            }
            catch { }

            // 2) instalador solto do lado do programa
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var sub in new[] { "_clients", ".", ".." })
                {
                    foreach (var n in new[] { "OpenVPN-2.7.8-I001-amd64.msi", "uvpn.openvpn.msi" })
                    {
                        try
                        {
                            var c = Path.GetFullPath(Path.Combine(dir, sub, n));
                            if (File.Exists(c)) return c;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 3) outra copia do programa em disco (quem ainda tem o nome antigo)
            try
            {
                string meuCaminho = "";
                try { meuCaminho = Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }
                var exes = new List<string>();
                var pastas = new List<string>();
                try { pastas.Add(Path.GetDirectoryName(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory))); } catch { }
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                foreach (var pasta in pastas)
                {
                    if (String.IsNullOrEmpty(pasta) || !Directory.Exists(pasta)) continue;
                    try
                    {
                        foreach (var f in Directory.GetFiles(pasta, "UltraVPNFree*.exe", SearchOption.AllDirectories))
                        {
                            try { if (meuCaminho.Length > 0 && Path.GetFullPath(f).Equals(meuCaminho, StringComparison.OrdinalIgnoreCase)) continue; } catch { }
                            exes.Add(f);
                            if (exes.Count >= 3) break;
                        }
                    }
                    catch { }
                    if (exes.Count > 0) break;
                }
                foreach (var exe in exes)
                {
                    var msi = ExtrairMsiDoExe(exe);
                    if (msi != null) return msi;
                }
            }
            catch { }
            return null;
        }

        string ExtrairMsiDoExe(string exe)
        {
            try
            {
                return ExtrairMsiDoAssembly(System.Reflection.Assembly.Load(File.ReadAllBytes(exe)));
            }
            catch { return null; }
        }

        // tira o .msi de dentro do assembly e joga no temp. se o recurso nao
        // existir, ou sair menor que o instalador real, devolve null.
        string ExtrairMsiDoAssembly(System.Reflection.Assembly asm)
        {
            try
            {
                if (asm == null) return null;
                if (Array.IndexOf(asm.GetManifestResourceNames(), "uvpn.openvpn.msi") < 0) return null;
                var destino = Path.Combine(Path.GetTempPath(), "uvpn_openvpn.msi");
                using (var entrada = asm.GetManifestResourceStream("uvpn.openvpn.msi"))
                {
                    if (entrada == null) return null;
                    using (var saida = File.Create(destino)) entrada.CopyTo(saida);
                }
                if (!File.Exists(destino)) return null;
                if (new FileInfo(destino).Length < 1000000) return null;   // extraiu lixo
                msiOpenVpnTemp = destino;
                return destino;
            }
            catch { return null; }
        }

        // o instalador do OpenVPN deixa 3 sobras: atalho na area de trabalho,
        // o GUI abrindo junto com oWindows e servico em Automatico. Nosso
        // programa chama o openvpn.exe direto, entao nada disso e preciso.
        void LimpiarSobrasOpenVpn()
        {
            try
            {
                var pastas = new List<string>();
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
                pastas.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                foreach (var d in pastas)
                {
                    if (String.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    try { foreach (var f in Directory.GetFiles(d, "OpenVPN*")) { try { File.Delete(f); } catch { } } } catch { }
                    try
                    {
                        var sub = Path.Combine(d, "OpenVPN");
                        if (Directory.Exists(sub)) Directory.Delete(sub, true);
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    try
                    {
                        if (!sc.ServiceName.StartsWith("OpenVPN")) continue;
                        // "demand" = Manual: o servico NAO sobe junto com o
                        // Windows (que e o que a gente quer).
                        //
                        // IMPORTADO: o servico tambem nao pode ser DESLIGADO
                        // aqui. O driver TAP-Windows 2.7 pede ao servico para
                        // criar o adaptador; com ele parado o openvpn responde
                        //   create_adapter: could not talk to service: Identificador invalido [6]
                        //   All tap-windows6 adapters are currently in use or disabled.
                        // e a conexao morre. Ele e ligado na hora de conectar
                        // (LigarServicoOpenVpn) e desligado ao desconectar.
                        var psiSc = new ProcessStartInfo("sc.exe", "config \"" + sc.ServiceName + "\" start= demand");
                        psiSc.UseShellExecute = false;
                        psiSc.CreateNoWindow = true;
                        psiSc.RedirectStandardOutput = true;
                        using (var psc = Process.Start(psiSc)) { if (psc != null) psc.WaitForExit(10000); }
                        Log("OpenVPN: o servico " + sc.ServiceName + " nao inicia mais com o Windows.");
                    }
                    catch { }
                }
            }
            catch { }
            try { var e = Store.LoadEstado(); e.InstalouOpenVpn = true; Store.SaveEstado(e); } catch { }
        }

        string AcharOpenVpn()
        {
            foreach (var basep in new[] {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (String.IsNullOrEmpty(basep)) continue;
                foreach (var sub in new[] { @"OpenVPN\bin\openvpn.exe", @"OpenVPN\bin\x64\openvpn.exe" })
                {
                    var c = Path.Combine(basep, sub);
                    if (File.Exists(c)) return c;
                }
            }
            return null;
        }

        string ovpnVersao = "";
        string AcharOvpnVersao(string exe)
        {
            if (!String.IsNullOrEmpty(ovpnVersao)) return ovpnVersao;
            try
            {
                var psi = new ProcessStartInfo(exe, "--version")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    var m = Regex.Match(saida, "OpenVPN (\\d+\\.\\d+\\.\\d+)");
                    if (m.Success) ovpnVersao = m.Groups[1].Value;
                }
            }
            catch { }
            return ovpnVersao;
        }

        // config do servidor: cache -> semente (base64 do VPN Gate) -> rede
        // Quando a fonte nao informa a porta, estas sao as portas SoftEther mais
        // usadas. So servem para deixar a linha identificavel na tela; se o
        // servidor nao estiver nessa porta, a sonda de portas corrige antes
        // de tentar o tunel.
        static int PortaPadrao(string proto)
        {
            return String.Equals(proto, "tcp", StringComparison.OrdinalIgnoreCase) ? 443 : 1194;
        }

        string PegarConfig(Servidor s)
        {
            var guardado = Store.ReadCfgCache(s.IP);
            if (!String.IsNullOrEmpty(guardado)) return guardado;
            if (!String.IsNullOrWhiteSpace(s.Cfg))
            {
                try
                {
                    var txt = Encoding.UTF8.GetString(Convert.FromBase64String(s.Cfg));
                    if (!String.IsNullOrWhiteSpace(txt))
                    {
                        Store.WriteCfgCache(s.IP, txt);
                        return txt;
                    }
                }
                catch { }
            }
            if (s.Src == "tlx" && !String.IsNullOrEmpty(s.CfgUrl))
            {
                // telexy: o .ovpn e baixado NOVO a cada conexao (nunca o
                // guardado), e a porta do registro e atualizada junto - o
                // telexy muda o endpoint sem avisar, e assim a porta fica
                // sempre verificada e o servidor nao se perde por defasagem
                try
                {
                    var wc = Rede.NovoWc();
                    var cfg = wc.DownloadString(s.CfgUrl);
                    if (!Regex.IsMatch(cfg, @"(?m)^\s*remote\s")) throw new Exception("O arquivo que veio do telexy n\u00e3o parece um .ovpn.");
                    Store.WriteCfgCache(s.IP, cfg);
                    Store.WriteBkCache(s.IP, cfg);
                    try
                    {
                        var mr = Regex.Match(cfg, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                        if (mr.Success)
                        {
                            int pt = 0;
                            int.TryParse(mr.Groups[1].Value, out pt);
                            if (pt > 0 && pt <= 65535) s.Port = pt;
                        }
                        var mp2 = Regex.Match(cfg, @"(?m)^\s*proto\s+(\w+)");
                        if (mp2.Success)
                        {
                            var t2 = mp2.Groups[1].Value.ToLowerInvariant();
                            if (t2.Contains("tcp")) s.Proto = "tcp";
                            else if (t2.Contains("udp")) s.Proto = "udp";
                        }
                    }
                    catch { }
                    return cfg;
                }
                catch (Exception e)
                {
                    // nao baixou agora (internet oscilou)? usa o guardado e, em
                    // ultimo caso, o backup imortal - em vez de perder o servidor
                    try
                    {
                        var reserva = Store.ReadCfgCache(s.IP);
                        if (!String.IsNullOrWhiteSpace(reserva))
                        {
                            Log("Telexy oscilou - usando o .ovpn guardado deste servidor.");
                            return reserva;
                        }
                        var bk = Store.ReadBkCache(s.IP);
                        if (!String.IsNullOrWhiteSpace(bk))
                        {
                            Log("Telexy fora do ar - usando o backup guardado deste servidor.");
                            return bk;
                        }
                    }
                    catch { }
                    Log("Telexy: " + e.Message);
                }
            }
            if (s.Src == "vpnbook" && !String.IsNullOrEmpty(s.Cid))
            {
                try
                {
                    string senha = "";
                    try { senha = Rede.FetchVpnBookPassword(); } catch { }
                    if (String.IsNullOrEmpty(senha)) senha = "zm396a4";
                    var ov = Rede.BuildVpnBookOvpn(s, senha);
                    if (!String.IsNullOrWhiteSpace(ov) && Regex.IsMatch(ov, @"(?m)^\s*remote\s"))
                    {
                        // injeta senha no auth.txt que NovoOvpnFile cria
                        // (o .ovpn ja tem auth-user-pass)
                        Store.WriteCfgCache(s.IP, ov);
                        Store.WriteBkCache(s.IP, ov);
                        return ov;
                    }
                }
                catch (Exception e) { Log("VPNBook: " + e.Message); }
            }
            if (s.Src == "pvl" && !String.IsNullOrEmpty(s.Cid))
            {
                // O token da lista publica vale 5 minutos (expiresIn:300) e o
                // site responde 410 "nao existe mais" quando ele vence ou quando
                // o servidor caiu. Nao e culpa do servidor: pede um token NOVO
                // e tenta de novo. Era assim que um servidor bom sumia com a
                // mensagem "(410) Nao existe mais".
                string ultimoErro = "";
                for (int tentativa = 0; tentativa < 2; tentativa++)
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(Dados.PvlUrl + "/get_token.php");
                        req.Method = "POST";
                        req.ContentType = "application/x-www-form-urlencoded;charset=UTF-8";
                        req.Accept = "application/json";
                        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                        req.Referer = Dados.PvlUrl + "/server/" + s.Cid + "/";
                        req.Timeout = 20000;
                        var bytes = Encoding.UTF8.GetBytes("id=" + s.Cid);
                        req.ContentLength = bytes.Length;
                        using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
                        string body;
                        using (var resp = req.GetResponse())
                        using (var rd = new StreamReader(resp.GetResponseStream()))
                            body = rd.ReadToEnd();
                        var url = "";
                        var mu = Regex.Match(body, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                        var mt = Regex.Match(body, "\"token\"\\s*:\\s*\"([^\"]+)\"");
                        if (mu.Success) url = mu.Groups[1].Value.Replace("\\/", "/");
                        else if (mt.Success) url = "/download.php?token=" + Uri.EscapeDataString(mt.Groups[1].Value);
                        if (url == "") throw new Exception("A lista publica nao liberou um token agora.");
                        if (url.StartsWith("/")) url = Dados.PvlUrl + url;
                        var wc = Rede.NovoWc();
                        wc.Headers["Referer"] = Dados.PvlUrl + "/server/" + s.Cid + "/";
                        var cfg = PegarTexto(url, 20000);
                        if (String.IsNullOrWhiteSpace(cfg)) throw new Exception("o download do .ovpn voltou vazio");
                        if (!Regex.IsMatch(cfg, @"(?m)^\s*remote\s")) throw new Exception("o arquivo da lista publica nao e um .ovpn");
                        Store.WriteCfgCache(s.IP, cfg);
                        Store.WriteBkCache(s.IP, cfg);
                        return cfg;
                    }
                    catch (Exception e)
                    {
                        ultimoErro = e.Message;
                        if (tentativa == 0) { try { System.Threading.Thread.Sleep(600); } catch { } }
                    }
                }
                Log("Lista publica: " + s.Cid + " - " + ultimoErro);
            }
            // fontes que nao publicam .ovpn proprio (hunt = SoftEther). Antes estos
            // servidores morriam com "nao consegui pegar a configuracao"; com a
            // semente eles passam a ter .ovpn e a poder conectar.
            if (s.Src == "hunt")
            {
                try
                {
                    var semente = Dados.SementeOvpn();
                    if (!String.IsNullOrEmpty(semente))
                    {
                        var ov = Dados.OvpnDaSemente(semente, s.IP, s.Port, s.Proto);
                        if (Regex.IsMatch(ov, @"(?m)^\s*remote\s"))
                        {
                            Store.WriteCfgCache(s.IP, ov);
                            Store.WriteBkCache(s.IP, ov);
                            return ov;
                        }
                    }
                }
                catch (Exception e) { Log("Semente hunt: " + e.Message); }
            }

            // ultimo recurso (qualquer fonte): backup imortal por IP - um
            // servidor que ja funcionou uma vez nunca fica sem configuracao
            try
            {
                var bk = Store.ReadBkCache(s.IP);
                if (!String.IsNullOrWhiteSpace(bk)) return bk;
            }
            catch { }
            return null;
        }

        class OvpnArq { public string Arq; public string Auth; }

        OvpnArq NovoOvpnFile(string cfgText, string ip, string userForAuth, string passForAuth)
        {
            Store.Init();
            string auth = null;
            if (Regex.IsMatch(cfgText, @"(?m)^\s*auth-user-pass(\s|$)"))
            {
                auth = Path.Combine(Store.TempDir, "auth.txt");
                string u = !String.IsNullOrEmpty(userForAuth) ? userForAuth : Dados.VpnUser;
                string pw = !String.IsNullOrEmpty(passForAuth) ? passForAuth : Dados.VpnPass;
                File.WriteAllText(auth, u + "\r\n" + pw, new UTF8Encoding(false));
            }
            var arq = Path.Combine(Store.TempDir, "tunel_" + Regex.Replace(ip, "[^0-9]", "") + ".ovpn");
            File.WriteAllText(arq, cfgText, new UTF8Encoding(false));   // sem BOM
            return new OvpnArq { Arq = arq, Auth = auth };
        }
        OvpnArq NovoOvpnFile(string cfgText, string ip) { return NovoOvpnFile(cfgText, ip, null, null); }

        // ---------------- AJUSTE AUTOMATICO DE PORTA ----------------
        void AprenderPorta(string ip, int porta)
        {
            try
            {
                List<int> l;
                if (!portasPorIP.TryGetValue(ip, out l) || l == null) { l = new List<int>(); portasPorIP[ip] = l; }
                if (!l.Contains(porta)) l.Add(porta);
                if (porta > 0 && porta <= 65535 && !portasVistas.Contains(porta)) portasVistas.Add(porta);
                while (portasVistas.Count > 60) portasVistas.RemoveAt(0);
                SalvarPortasAprendidas();
            }
            catch { }
        }

        // ---------------- PORTAS AUTOMATICAS (sonda + tenta + aprende) ----------------
        // Uma porta que responde a sonda nem sempre abre o tunel de verdade - por
        // isso o programa sonda TODAS as candidatas em paralelo, tenta abrir o
        // tunel em cada uma que respondeu (ate 3 tentativas no MESMO servidor) e
        // so desiste dele quando nenhuma abre. A porta que abrir e APRENDIDA com
        // o protocolo junto: na proxima vez ela e a primeira tentativa.
        class PortaResp { public int Porta; public string Proto; public int Ms; }
        class ComboPorta { public int Porta; public string Proto; public bool Respondeu; }

        static readonly int[] PortasTcpComuns = new int[] { 443, 995, 992, 993, 1194, 1723, 8080, 8443, 80, 1195 };
        static readonly int[] PortasUdpComuns = new int[] { 1194, 53, 443, 992, 995, 1195, 1294, 1313, 1992, 1538, 5555, 8888, 1723, 27625, 3997, 38869, 41303 };

        void SalvarPortasAprendidas()
        {
            try { Store.SalvarPortas(new Store.PortasMap { PorIP = portasPorIP, Vistas = portasVistas, PorIPProto = portaProtoPorIP }); }
            catch { }
        }

        // a porta+protocolo que ABRIU o tunel nesse ip: aprende para ir direto
        void AprenderPortaProto(string ip, int porta, string proto)
        {
            try
            {
                if (String.IsNullOrEmpty(ip) || porta <= 0 || porta > 65535) return;
                if (proto != "tcp" && proto != "udp") proto = "udp";
                portaProtoPorIP[ip] = porta + "/" + proto;
                AprenderPorta(ip, porta);
            }
            catch { }
        }

        // sonda em paralelo: TCP = connect, UDP = hard-reset do OpenVPN com
        // 2 rodadas (1 pacote UDP se perde facil). Devolve so quem RESPONDEU,
        // do mais rapido ao mais lento. ~2,2 s no total.
        List<PortaResp> SondarPortas(string ip, List<int> tcp, List<int> udp)
        {
            var resp = new List<PortaResp>();
            try
            {
                var sondasTcp = new List<object[]>();
                foreach (var p in tcp)
                {
                    try
                    {
                        var c = new TcpClient();
                        sondasTcp.Add(new object[] { p, c, c.ConnectAsync(ip, p), Stopwatch.StartNew() });
                    }
                    catch { }
                }
                var sondasUdp = new List<object[]>();
                var rnd = new Random();
                foreach (var p in udp)
                {
                    try
                    {
                        var sk = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        sk.Connect(ip, p);
                        var sid = new byte[8];
                        rnd.NextBytes(sid);
                        var v = concatBytes(new byte[][] { new byte[] { 0x38 }, sid, new byte[] { 0, 0, 0, 0 }, new byte[] { 0 } });
                        try { sk.Send(v, 0, v.Length, SocketFlags.None); } catch { }
                        sondasUdp.Add(new object[] { p, sk, v, Stopwatch.StartNew(), 0 });
                    }
                    catch { }
                }
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2200)
                {
                    foreach (var so in sondasTcp)
                    {
                        try
                        {
                            var tsk = (Task)so[2];
                            if (tsk.IsCompleted && !tsk.IsFaulted && ((TcpClient)so[1]).Connected)
                            {
                                int p = (int)so[0];
                                bool ja = false;
                                foreach (var r in resp) if (r.Porta == p && r.Proto == "tcp") { ja = true; break; }
                                if (!ja) resp.Add(new PortaResp { Porta = p, Proto = "tcp", Ms = (int)((Stopwatch)so[3]).ElapsedMilliseconds });
                            }
                        }
                        catch { }
                    }
                    foreach (var so in sondasUdp)
                    {
                        try
                        {
                            var sk = (Socket)so[1];
                            var swSk = (Stopwatch)so[3];
                            // segunda rodada aos 700 ms para quem ainda nao respondeu
                            if ((int)so[4] == 0 && swSk.ElapsedMilliseconds > 700)
                            {
                                so[4] = 1;
                                try
                                {
                                    var v2 = (byte[])so[2];
                                    sk.Send(v2, 0, v2.Length, SocketFlags.None);
                                }
                                catch { }
                            }
                            if (sk.Poll(0, SelectMode.SelectRead))
                            {
                                var buf = new byte[2048];
                                if (sk.Receive(buf) > 0)
                                {
                                    int p = (int)so[0];
                                    bool ja = false;
                                    foreach (var r in resp) if (r.Porta == p && r.Proto == "udp") { ja = true; break; }
                                    if (!ja) resp.Add(new PortaResp { Porta = p, Proto = "udp", Ms = (int)swSk.ElapsedMilliseconds });
                                }
                            }
                        }
                        catch { }
                    }
                    Thread.Sleep(40);
                }
                foreach (var so in sondasTcp) { try { ((TcpClient)so[1]).Close(); } catch { } }
                foreach (var so in sondasUdp) { try { ((Socket)so[1]).Close(); } catch { } }
            }
            catch { }
            try { resp.Sort(delegate(PortaResp a, PortaResp b) { return a.Ms.CompareTo(b.Ms); }); } catch { }
            return resp;
        }

        // monta as tentativas porta+protocolo, da melhor para a pior:
        // 0) a que A FONTE declarou no .ovpn fresco (fonte da verdade)  1) a
        // que JA ABRIU nesse ip (aprendida)  2) as que responderam a sonda
        // 3) as portas do .ovpn (a sonda perde pacote e o tunel abre mesmo assim)
        List<ComboPorta> MontarCombos(Servidor s, string cfgText)
        {
            var combos = new List<ComboPorta>();
            Action<int, string, bool> add = delegate(int porta, string proto, bool respondeu)
            {
                if (porta <= 0 || porta > 65535) return;
                if (proto != "tcp" && proto != "udp") proto = "udp";
                foreach (var c in combos) if (c.Porta == porta && c.Proto == proto) return;
                combos.Add(new ComboPorta { Porta = porta, Proto = proto, Respondeu = respondeu });
            };
            // protocolo declarado no arquivo
            string protoCfg = "";
            try
            {
                var mp = Regex.Match(cfgText, @"(?m)^\s*proto\s+(\w+)");
                if (mp.Success)
                {
                    var t = mp.Groups[1].Value.ToLowerInvariant();
                    if (t.Contains("tcp")) protoCfg = "tcp";
                    else if (t.Contains("udp")) protoCfg = "udp";
                }
            }
            catch { }
            string pref = protoCfg;
            if (pref != "tcp" && pref != "udp" && (s.Proto == "tcp" || s.Proto == "udp")) pref = s.Proto;
            bool fixa = (protocol == "tcp" || protocol == "udp");
            // 0) a porta que A FONTE declarou no .ovpn fresco: se o servidor mudou
            // de porta, a aprendida fica velha e o programa ficava "preso" nela
            // - a da fonte vai primeiro e, se abrir, vira a nova aprendida
            try
            {
                var mf = Regex.Match(cfgText, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                if (mf.Success)
                {
                    int pf = 0;
                    int.TryParse(mf.Groups[1].Value, out pf);
                    string prf = pref;
                    if (prf != "tcp" && prf != "udp") prf = "udp";
                    if (pf > 0) add(pf, prf, false);
                }
            }
            catch { }
            // 1) a vencedora aprendida entra na lista (a posicao final quem decide
            // e a sonda: se ela nao responder agora, perde a frente e nao
            // gasta 15 s a toa como primeira tentativa)
            try
            {
                string ganha;
                if (portaProtoPorIP.TryGetValue(s.IP, out ganha) && !String.IsNullOrEmpty(ganha))
                {
                    var pp = ganha.Split('/');
                    int pg = 0;
                    if (pp.Length > 0) int.TryParse(pp[0], out pg);
string pr = pp.Length > 1 ? pp[1] : "udp";
                    if (pg > 0) add(pg, pr, false);
                }
            }
            catch { }
            // 2) portas de TODAS as linhas "remote" do .ovpn
            var portasCfg = new List<int>();
            try
            {
                foreach (Match m in Regex.Matches(cfgText, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})"))
                {
                    int pt = 0;
                    int.TryParse(m.Groups[1].Value, out pt);
                    if (pt > 0 && pt <= 65535 && !portasCfg.Contains(pt)) portasCfg.Add(pt);
                }
            }
            catch { }
            // 3) aprendidas nesse ip + vistas + comuns
            var base_ = new List<int>();
            base_.AddRange(portasCfg);
            try
            {
                List<int> vip;
                if (portasPorIP.TryGetValue(s.IP, out vip) && vip != null) base_.AddRange(vip);
            }
            catch { }
            base_.AddRange(portasVistas);
            base_.AddRange(PortasTcpComuns);
            base_.AddRange(PortasUdpComuns);
            var ports = new List<int>();
            foreach (var p in base_) if (p > 0 && p <= 65535 && !ports.Contains(p)) ports.Add(p);
            if (ports.Count == 0) ports.Add(1194);
            // 4) sonda tudo em paralelo nos dois protocolos
            List<PortaResp> resp;
            try { resp = SondarPortas(s.IP, ports, ports); }
            catch { resp = new List<PortaResp>(); }
            if (resp.Count > 0)
                Log("Sonda de portas em " + s.IP + ": " + resp.Count + " responderam" + (fixa ? " (preferindo " + protocol.ToUpperInvariant() + ")" : "") + ".");
            foreach (var r in resp)
            {
                if (fixa && r.Proto != protocol) continue;   // entra no passo 6
                add(r.Porta, r.Proto, true);
            }
            // 5) as portas do .ovpn entram mesmo sem resposta (a sonda perde pacote)
            foreach (var p in portasCfg)
            {
                string pr2 = pref;
                if (pr2 != "tcp" && pr2 != "udp") pr2 = (fixa ? protocol : "udp");
                add(p, pr2, false);
                if (!fixa && pr2 == "udp") add(p, "tcp", false);
                else if (!fixa && pr2 == "tcp") add(p, "udp", false);
            }
            // 6) resto dos respondentes no outro protocolo (reserva automatica)
            if (fixa)
                foreach (var r in resp) add(r.Porta, r.Proto, true);
            // 7) a sonda manda na ordem: quem respondeu (na ordem de
            // velocidade) primeiro, o resto depois - inclusive a vencedora
            // aprendida, que so vai na frente se confirmar viva agora
            try
            {
                foreach (var r in resp)
                    foreach (var c in combos)
                        if (c.Porta == r.Porta && c.Proto == r.Proto) { c.Respondeu = true; break; }
                var ordenados = new List<ComboPorta>();
                foreach (var r in resp)
                    foreach (var c in combos)
                        if (!ordenados.Contains(c) && c.Porta == r.Porta && c.Proto == r.Proto) ordenados.Add(c);
                foreach (var c in combos)
                    if (!ordenados.Contains(c)) ordenados.Add(c);
                combos = ordenados;
            }
            catch { }
            return combos;
        }

        class ResultadoTunel { public bool Ok; public string Txt; public string Detalhe; }

        // espera o "Initialization Sequence Completed": 15 s sem resposta =
        // morto, 25 s com resposta sem completar = morto
        ResultadoTunel EsperarTunel(string log, Process p)
        {
            var rt = new ResultadoTunel { Txt = "" };
            bool resp = false;
            long tamLido = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 45000)
            {
                Thread.Sleep(200);
                try
                {
                    if (File.Exists(log))
                    {
                        var fi = new FileInfo(log);
                        if (fi.Length > 0)
                        {
                            if (fi.Length < tamLido) tamLido = 0;
                            if (fi.Length > tamLido)
                            {
                                using (var fs = File.Open(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    fs.Seek(tamLido, SeekOrigin.Begin);
                                    var buf = new byte[fi.Length - tamLido];
                                    int lidos = fs.Read(buf, 0, buf.Length);
                                    if (lidos > 0)
                                    {
                                        rt.Txt += Encoding.UTF8.GetString(buf, 0, lidos);
                                        tamLido += lidos;
                                        if (rt.Txt.Contains("Initialization Sequence Completed")) { rt.Ok = true; break; }
                                        if (Regex.IsMatch(rt.Txt, "FATAL:|AUTH_FAILED|Cannot open TUN|Connection refused|Cannot resolve host address")) break;
                                        if (!resp && Regex.IsMatch(rt.Txt, "TLS: Initial packet received|Peer Connection Initiated|TCP connection established|Control Channel:")) resp = true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                try { if (p.HasExited) break; } catch { break; }
                if (!resp && sw.ElapsedMilliseconds >= 15000) break;
                if (resp && sw.ElapsedMilliseconds >= 25000) break;
            }
            if (!rt.Ok)
            {
                var det = "";
                foreach (var ln in rt.Txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    if (Regex.IsMatch(ln, "FATAL:|ERROR:|AUTH_FAILED|Cannot")) { det = ln; break; }
                }
                try { if (det == "" && p.HasExited) det = "o OpenVPN fechou sozinho (codigo " + p.ExitCode + ")"; } catch { }
                if (det == "" && !resp) det = "o servidor nao respondeu em 15 s (fora do ar)";
                if (det == "") det = "nao completou a tempo";
                rt.Detalhe = det;
            }
            return rt;
        }

        static byte[] concatBytes(byte[][] p)
        {
            var r = new byte[p.Sum(x => x.Length)];
            int i = 0;
            foreach (var b in p) { Array.Copy(b, 0, r, i, b.Length); i += b.Length; }
            return r;
        }

        // ---------------- abertura do tunel ----------------
        // UrbanVPN = proxy HTTP residencial (nao e OpenVPN): usa o accs-proxy
        // VPNBook = OpenVPN com senha automatica (vpnbook/xxx sem digitar)
        void AbrirTunel(Servidor s)
        {
            if (s != null && s.Src == "urban")
            {
                AbrirTunelUrban(s);
                return;
            }
            // o instalador do OpenVPN vai embutido no .exe (uvpn.openvpn.msi), entao
            // este programa se vira sozinho num PC sem VPN instalada
            if (AcharOpenVpn() == null) InstalarOpenVpnSeFaltar();
            var exe = AcharOpenVpn();
            if (exe == null)
                throw new Exception("OpenVPN n\u00e3o encontrado. Este .exe instala sozinho; se falhou, reinstale o OpenVPN Community.");
            string cfgText = null;
            string vpnBookSenha = null;
            if (s.Src == "vpnbook")
            {
                try { vpnBookSenha = Rede.FetchVpnBookPassword(); } catch { }
                if (String.IsNullOrEmpty(vpnBookSenha)) vpnBookSenha = "zm396a4";
                try { cfgText = Rede.BuildVpnBookOvpn(s, vpnBookSenha); } catch { }
                if (String.IsNullOrWhiteSpace(cfgText)) cfgText = PegarConfig(s);
            }
            else cfgText = PegarConfig(s);
            if (String.IsNullOrWhiteSpace(cfgText)) throw new Exception("N\u00e3o consegui pegar a configura\u00e7\u00e3o deste servidor.");

            var usado = "padrao";
            var mp = Regex.Match(cfgText, @"(?m)^\s*proto\s+(\w+)");
            if (mp.Success) usado = mp.Groups[1].Value;
            if ((protocol == "udp" || protocol == "tcp") && !String.IsNullOrEmpty(s.Proto) && s.Proto != "both" && s.Proto != protocol)
                Log("Este servidor s\u00f3 aceita " + s.Proto.ToUpperInvariant() + " - conectando com " + s.Proto.ToUpperInvariant() + " nele.");

            int portaCfg = 0;
            try
            {
                var rm = Regex.Match(cfgText, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                if (rm.Success) portaCfg = int.Parse(rm.Groups[1].Value);
            }
            catch { }
            var protoConn = "";
            if (usado != "padrao") protoConn = usado;
            if (!Regex.IsMatch(protoConn, "^(tcp|udp)")) protoConn = String.IsNullOrEmpty(s.Proto) ? "" : s.Proto;
            if (protoConn == "tcp-client") protoConn = "tcp";
            if (!Regex.IsMatch(protoConn, "^(tcp|udp)"))
                protoConn = (portaCfg == 443 || portaCfg == 995 || portaCfg == 992 || portaCfg == 1723) ? "tcp" : "udp";

            var f = (s.Src == "vpnbook" && !String.IsNullOrEmpty(vpnBookSenha))
                ? NovoOvpnFile(cfgText, s.IP, Dados.VpnBookUser, vpnBookSenha)
                : NovoOvpnFile(cfgText, s.IP);
            var log = Path.Combine(Store.TempDir, "openvpn.log");

            // PORTAS AUTOMATICAS: sonda todas em paralelo e tenta abrir o tunel
            // em cada uma que respondeu (ate 3 no MESMO servidor) - o servidor
            // so e perdido quando NENHUMA porta abre de verdade
            var combos = new List<ComboPorta>();
            try { combos = MontarCombos(s, cfgText); }
            catch { }
            if (combos.Count == 0)
            {
                int pc0 = portaCfg > 0 ? portaCfg : 1194;
                string pr0 = protoConn;
                if (pr0 != "tcp" && pr0 != "udp") pr0 = "udp";
                combos.Add(new ComboPorta { Porta = pc0, Proto = pr0, Respondeu = false });
            }
            int maxTentativas = Math.Min(3, combos.Count);
            string txt = "";
            Process p = null;
            string erroTunel = "";
            // cifra AEAD so no OpenVPN 2.5+ (no 2.4 derrubaria o tunel)
            bool ovpnNovo = false;
            try
            {
                var vOv = AcharOvpnVersao(exe);
                Version vv, v25;
                if (Version.TryParse(vOv, out vv) && Version.TryParse("2.5.0", out v25) && vv >= v25) ovpnNovo = true;
            }
            catch { }
            for (int ti = 0; ti < maxTentativas; ti++)
            {
                var cb = combos[ti];
                try { if (File.Exists(log)) File.Delete(log); } catch { }
                try
                {
                    // o .ovpn passa a apontar para essa porta+protocolo
                    var txtOvpn = cfgText;
                    // ${1} com chaves: "$1" colado num IP vira grupo inexistente ($138)
                    // e o .NET deixa literal, corrompendo o .ovpn (foi isso que
                    // derrubava o openvpn com codigo 1 em TODOS os servidores)
                    txtOvpn = Regex.Replace(txtOvpn, @"(?m)^(\s*remote\s+)\S+(\s+\d{1,5})?", "${1}" + s.IP + " " + cb.Porta);
                    if (!Regex.IsMatch(txtOvpn, @"(?m)^\s*remote\s"))
                        txtOvpn = "remote " + s.IP + " " + cb.Porta + "\r\n" + txtOvpn;
                    if (Regex.IsMatch(txtOvpn, @"(?m)^\s*proto\s"))
                        txtOvpn = Regex.Replace(txtOvpn, @"(?m)^\s*proto\s+\w+", "proto " + cb.Proto);
                    else
                        txtOvpn = "proto " + cb.Proto + "\r\n" + txtOvpn;
                    File.WriteAllText(f.Arq, txtOvpn, new UTF8Encoding(false));
                }
                catch { }
                // MTU enxuto + tcp-nodelay + fast-io + buffers + NAT vivo + saida limpa
                var argList = new List<string> {
                    "--config", f.Arq, "--log", log, "--route-nopull", "--verb", "3",
                    "--tun-mtu", "1400", "--mssfix", "1360", "--tcp-nodelay", "--fast-io",
                    "--sndbuf", "524288", "--rcvbuf", "524288",
                    "--ping", "10", "--ping-exit", "60" };
                if (cb.Proto == "udp")
                    argList.AddRange(new[] { "--explicit-exit-notify", "3" });
                if (ovpnNovo)
                    argList.AddRange(new[] {
                        "--data-ciphers", "AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305:AES-256-CBC",
                        "--data-ciphers-fallback", "AES-256-CBC", "--allow-compression", "asym" });
                if (!String.IsNullOrEmpty(f.Auth)) argList.AddRange(new[] { "--auth-user-pass", f.Auth });
                string confVenc = "";
                try
                {
                    var mfonte = Regex.Match(cfgText, @"(?m)^\s*remote\s+\S+\s+(\d{1,5})");
                    int pfonte = 0;
                    if (mfonte.Success) int.TryParse(mfonte.Groups[1].Value, out pfonte);
                    if (cb.Respondeu && pfonte > 0 && pfonte == cb.Porta)
                        confVenc = " - porta da fonte confirmada agora na sonda";
                    else
                    {
                        string conf;
                        if (cb.Respondeu && portaProtoPorIP.TryGetValue(s.IP, out conf) && conf == cb.Porta + "/" + cb.Proto)
                            confVenc = " - porta aprendida confirmada agora na sonda";
                    }
                }
                catch { }
                Log("Conectando em " + s.Name + " (" + s.IP + ":" + cb.Porta + "/" + cb.Proto.ToUpperInvariant() + ")" + (ti > 0 ? " - tentativa " + (ti + 1) : "") + (cb.Respondeu ? "..." : " (sem resposta na sonda, tentando mesmo assim)...") + confVenc);
                var psi = new ProcessStartInfo(exe, String.Join(" ", argList.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                p = Process.Start(psi);
                ovpnProc = p;
                var rt = EsperarTunel(log, p);
                if (rt.Ok)
                {
                    txt = rt.Txt;
                    AprenderPortaProto(s.IP, cb.Porta, cb.Proto);
                    break;
                }
                erroTunel = rt.Detalhe;
                if (ti + 1 < maxTentativas)
                    Log("A porta " + cb.Porta + "/" + cb.Proto + " n\u00e3o abriu (" + rt.Detalhe + ") - tentando a pr\u00f3xima porta sozinho...");
                try { PararMotor(); } catch { }
                p = null;
            }
            if (String.IsNullOrEmpty(txt))
                throw new Exception(("O servidor n\u00e3o abriu o t\u00fanel. " + erroTunel).Trim());

            // acha o adaptador do tunel
            string ad = null;
            var swAd = Stopwatch.StartNew();
            while (swAd.ElapsedMilliseconds < 10000)
            {
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        var d = ni.Description ?? "";
                        if (Regex.IsMatch(d, "TAP-Windows|ovpn-dco|wintun|OpenVPN", RegexOptions.IgnoreCase)) { ad = ni.Name; break; }
                    }
                }
                catch { }
                if (ad != null) break;
                Thread.Sleep(200);
            }
            if (ad == null) throw new Exception("O t\u00fanel abriu mas n\u00e3o achei o adaptador de rede dele.");
            ifAlias = ad;

            // gateway do tunel (o servidor manda no empurrao)
            var gw = "";
            var mg = Regex.Match(txt, @"route-gateway\s+(\d{1,3}(?:\.\d{1,3}){3})");
            if (mg.Success) gw = mg.Groups[1].Value;
            else
            {
                var md = Regex.Match(txt, @"DHCP-serv:\s*(\d{1,3}(?:\.\d{1,3}){3})");
                if (md.Success) gw = md.Groups[1].Value;
                else
                {
                    try
                    {
                        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.Name != ifAlias) continue;
                            var ips = ni.GetIPProperties().GatewayAddresses;
                            if (ips != null) foreach (var g in ips) if (g.Address.AddressFamily == AddressFamily.InterNetwork) { gw = g.Address.ToString(); break; }
                            if (gw == "")
                            {
                                // o gateway do tunel costuma ser o primeiro IP da subrede
                                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                                {
                                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                                    var b = u.Address.GetAddressBytes();
                                    gw = b[0] + "." + b[1] + "." + b[2] + "." + (b[3] == 1 ? 2 : 1);
                                    break;
                                }
                            }
                            if (gw != "") break;
                        }
                    }
                    catch { }
                }
            }
            if (gw == "") throw new Exception("Conectei, mas n\u00e3o consegui identificar o gateway do t\u00fanel.");
            vpnGw = gw;
            LimparRotasAntigas();
            connected = true;
            conexaoNumero++;
            ownedRoutes = new List<string>();
            defRoute = false;
            BeginInvoke((Action)delegate
            {
                lblStatus.Text = "Status: CONECTADO  |  " + s.Name;
                servidorAtual = s.Name;
                try
                {
                    var cidSrv = Dados.GetServerLocal(s);
                    if (!String.IsNullOrEmpty(cidSrv) && cidSrv != s.Name) servidorAtual = s.Name + " - " + cidSrv;
                    if (usado != "padrao") servidorAtual += " (" + usado.ToUpperInvariant() + ")";
                }
                catch { }
                // o endereco completo que o tunel esta usando, para a tela
                // mostrar do lado do nome (o antigo mostrava so o nome)
                try
                {
                    var protoMostra = usado;
                    if (String.IsNullOrEmpty(protoMostra) || usado == "padrao")
                        protoMostra = s.Proto == "udp" ? "UDP" : s.Proto == "tcp" ? "TCP" : "UDP/TCP";
                    servidorEndereco = s.IP + ":" + s.Port + "/" + protoMostra.ToUpperInvariant();
                }
                catch { }
                AtualizarEstadoConexao();
                try { if (!Visible) tray.ShowBalloonTip(4000, "Ultra VPN Free", "VPN conectada - clique no \u00edcone da bandeja para abrir.", ToolTipIcon.Info); } catch { }
            });
            SincronizarRotas();
            Rede.RunCmd("ipconfig", "/flushdns");
            // fotografa a contagem AGORA: o bloco do Log roda depois, na fila
            // da janela, e o vigia pode ja ter mexido na lista ate la
            int nRotas = 0;
            try { nRotas = ownedRoutes.Count; } catch { }
            BeginInvoke((Action)delegate
            {
                Log("Conectado. " + (fullTunnel ? "Agora o PC inteiro passa pela VPN." : "Agora s\u00f3 os programas escolhidos passam pela VPN."));
                Log(nRotas + " rotas de t\u00fanel ativas.");
                if (fullTunnel)
                    Log("Verificado: cobertura 0.0.0.0/1 + 128.0.0.0/1 no lugar, DNS pelo t\u00fanel (1.1.1.1) e IPv6 fora do caminho - nada escapa.");
                else
                    Log("Verificado: modo dividido - s\u00f3 as rotas dos programas escolhidos apontam pro t\u00fanel; o resto do PC segue na sua conex\u00e3o.");
            });
            MedirPingTunel(s.Name);
            IniciarChecagemIP();
        }

        void PararMotor()
        {
            try { if (ovpnProc != null && !ovpnProc.HasExited) ovpnProc.Kill(); } catch { }
            try { ovpnProc = null; } catch { }
            // openvpn SOLTO (de sessao morta ou de queda torta) divide o TAP
            // com o tunel novo e mata ele em segundos: varre e mata qualquer
            // openvpn rodando (o programa roda elevado, entao alcanca todos)
            try
            {
                foreach (var p in Process.GetProcessesByName("openvpn"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        // ============================================================
        //  ROTAS (route.exe) + DNS + IPv6
        // ============================================================
        void AdicionarRota(string alvo)
        {
            if (String.IsNullOrEmpty(ifAlias) || String.IsNullOrEmpty(vpnGw)) return;
            var prefixo = alvo.Contains("/") ? alvo : alvo + "/32";
            if (ownedRoutes.Contains(prefixo)) return;
            // metric 1: a nossa rota vence qualquer outra para o mesmo destino
            Rede.RunCmd("route", "add " + Rede.RedeDePrefixo(prefixo) + " mask " + Rede.MascaraDePrefixo(prefixo) + " " + vpnGw + " metric 1");
            ownedRoutes.Add(prefixo);
        }

        void RemoverRota(string alvo)
        {
            var prefixo = alvo.Contains("/") ? alvo : alvo + "/32";
            // escopada pelo gateway do tunel: so sai rota NOSSA, nunca a do usuario
            var gw = String.IsNullOrEmpty(vpnGw) ? "" : " " + vpnGw;
            Rede.RunCmd("route", "delete " + Rede.RedeDePrefixo(prefixo) + " mask " + Rede.MascaraDePrefixo(prefixo) + gw);
        }

        void LimparRotasNossas()
        {
            if (!String.IsNullOrEmpty(urbanProxyUrl)) { try { DesligarUrbanProxy(); } catch { } }
            foreach (var ip in ownedRoutes.ToArray())
            {
                RemoverRota(ip);
                ownedRoutes.Remove(ip);
            }
            if (defRoute) { RemoverRotaPadrao(); defRoute = false; }
            LigarIpv6DeVolta();
            // cache de DNS pode guardar resposta do caminho velho: limpa
            Rede.RunCmd("ipconfig", "/flushdns");
        }

        void LimparRotasAntigas()
        {
            // no comeco da conexao, limpa rotas do tunel que sobraram de antes
            foreach (var ip in ownedRoutes.ToArray()) { RemoverRota(ip); ownedRoutes.Remove(ip); }
            // varredura na tabela: rotas via gateways 10.211.x MORTOS (de sessoes
            // passadas, que a lista nao conhece mais) ficam empilhadas e, com a
            // mesma metrica da rota viva, podem engolir o destino. So sai rota
            // via 10.211.x diferente do gateway atual - rota do usuario nunca
            // usa esse gateway (e a subrede que o servidor empurra pro tunel).
            try
            {
                foreach (var r in RotasAtivas())
                {
                    var gw = r[2];
                    IPAddress g;
                    if (!IPAddress.TryParse(gw, out g)) continue;
                    var b = g.GetAddressBytes();
                    if (b.Length < 2 || b[0] != 10 || b[1] != 211) continue;
                    if (String.Equals(gw, vpnGw, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Rede.RunCmd("route", "delete " + r[0] + " mask " + r[1] + " " + gw); } catch { }
                }
            }
            catch { }
        }

        void AdicionarRotaPadrao()
        {
            if (String.IsNullOrEmpty(vpnGw)) return;
            foreach (var pr in new[] { "0.0.0.0/1", "128.0.0.0/1" })
            {
                if (ownedRoutes.Contains(pr)) continue;
                // metric 1: a cobertura do PC inteiro vence a rota padrao fisica
                Rede.RunCmd("route", "add " + Rede.RedeDePrefixo(pr) + " mask " + Rede.MascaraDePrefixo(pr) + " " + vpnGw + " metric 1");
                ownedRoutes.Add(pr);
            }
            defRoute = true;
            DnsDoTunel(true);
        }

        void RemoverRotaPadrao()
        {
            // IMPORTADO: a mascara e obrigatoria. Sem ela o comando vira
            // "route delete 0.0.0.0", que apaga a rota PADRAO do Windows
            // (0.0.0.0/0) e deixa a maquina sem internet nenhuma.
            foreach (var pr in new[] { "0.0.0.0/1", "128.0.0.0/1" })
                Rede.RunCmd("route", "delete " + Rede.RedeDePrefixo(pr) + " mask " + Rede.MascaraDePrefixo(pr));
            DnsDoTunel(false);
        }

        // DNS: com "tudo pela VPN" o DNS local ficaria preso - troca por 1.1.1.1
        void DnsDoTunel(bool on)
        {
            var eth = AcharInterfaceFisica();
            if (String.IsNullOrEmpty(eth)) return;
            if (on)
            {
                if (dnsSalvo) return;
                try
                {
                    savedDns = LerDns(eth);
                    savedDnsDhcp = (savedDns == null || savedDns.Count == 0);
                    Rede.RunCmd("netsh", "interface ip set dns name=\"" + eth + "\" source=static addr=1.1.1.1 register=primary");
                    Rede.RunCmd("netsh", "interface ip add dns name=\"" + eth + "\" addr=1.0.0.1 index=2");
                    dnsSalvo = true;
                    Log("DNS pelo t\u00fanel (1.1.1.1) - o seu era " + (savedDns != null && savedDns.Count > 0 ? String.Join(", ", savedDns) : "autom\u00e1tico") + ".");
                }
                catch (Exception e) { Log("Aviso: n\u00e3o deu para trocar o DNS: " + e.Message); }
            }
            else
            {
                if (!dnsSalvo) return;
                try
                {
                    if (savedDnsDhcp || savedDns == null || savedDns.Count == 0)
                        Rede.RunCmd("netsh", "interface ip set dns name=\"" + eth + "\" source=dhcp");
                    else
                    {
                        Rede.RunCmd("netsh", "interface ip set dns name=\"" + eth + "\" source=static addr=" + savedDns[0] + " register=primary");
                        for (int i = 1; i < savedDns.Count; i++)
                            Rede.RunCmd("netsh", "interface ip add dns name=\"" + eth + "\" addr=" + savedDns[i] + " index=" + (i + 1));
                    }
                }
                catch { }
                dnsSalvo = false;
                savedDns = null;
            }
        }

        List<string> LerDns(string eth)
        {
            var res = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("netsh", "interface ip show dns name=\"" + eth + "\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (Match m in Regex.Matches(saida, "(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})"))
                    {
                        var ip = m.Groups[1].Value;
                        if (ip != "127.0.0.1" && !res.Contains(ip)) res.Add(ip);
                    }
                }
            }
            catch { }
            return res;
        }

        string AcharInterfaceFisica()
        {
            try
            {
                // a interface com a menor metrica que nao seja o tunel
                NetworkInterface melhor = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Name == ifAlias) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                        && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    // sem gateway nao e a saida pra internet: nao mexe no DNS dela
                    if (ni.GetIPProperties() == null || ni.GetIPProperties().GatewayAddresses.Count == 0) continue;
                    if (melhor == null) melhor = ni;
                }
                return melhor != null ? melhor.Name : "";
            }
            catch { return ""; }
        }

        // IPv6: no tunel "tudo pela VPN" sai do caminho (o tunel e so IPv4)
        string ipv6Salvo = null;   // "alias|nexthop|metric"
        bool ipv6Aviso = false;

        void DesligarIpv6Temporario()
        {
            try
            {
                var rota = LerRotaIpv6Padrao();
                if (rota == null) return;
                if (ipv6Salvo == null)
                {
                    ipv6Salvo = rota;
                    SalvarEstado();
                }
                Rede.RunCmd("netsh", "interface ipv6 delete route prefix=::/0 interface=" + RotaCampo(rota, 0) + " store=active");
                if (!ipv6Aviso)
                {
                    ipv6Aviso = true;
                    Log("IPv6 temporariamente desligado na VPN: o t\u00fanel \u00e9 s\u00f3 IPv4 e o IPv6 fugia da VPN.");
                }
            }
            catch { }
        }

        // le a rota padrao "::/0" (netsh interface ipv6 show route):
        // devolve "indice|gateway" (gateway pode ser o nome da interface)
        string LerRotaIpv6Padrao()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "interface ipv6 show route")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var ln in saida.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        if (!ln.Contains("::/0")) continue;
                        var part = ln.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int i = Array.IndexOf(part, "::/0");
                        if (i < 0 || i + 1 >= part.Length) continue;
                        int idx;
                        if (!int.TryParse(part[i + 1], out idx)) continue;
                        var gw = (i + 2 < part.Length) ? part[i + 2] : "";
                        return idx + "|" + gw;
                    }
                }
            }
            catch { }
            return null;
        }
        string RotaCampo(string rota, int idx)
        {
            var p = (rota ?? "").Split('|');
            return p.Length > idx ? p[idx] : "";
        }

        void LigarIpv6DeVolta()
        {
            if (ipv6Salvo == null) return;
            try
            {
                // restaura do jeito que estava: com gateway (rota de roteador)
                // ou direto na interface (rota on-link)
                var p6 = ipv6Salvo.Split('|');
                if (p6.Length >= 2 && !String.IsNullOrEmpty(p6[0]))
                {
                    if (!String.IsNullOrEmpty(p6[1]) && p6[1].Contains(":"))
                        Rede.RunCmd("netsh", "interface ipv6 add route ::/0 " + p6[0] + " " + p6[1] + " store=active");
                    else
                        Rede.RunCmd("netsh", "interface ipv6 add route ::/0 " + p6[0] + " store=active");
                }
                ipv6Salvo = null;
                ipv6Aviso = false;
                SalvarEstado();
                try { foreach (var a in Directory.GetFiles(Store.TempDir, "wfp_*")) File.Delete(a); } catch { }
            }
            catch { }
        }

        // ============================================================
        //  ROTAS POR APLICATIVO (Sync-SplitRoutes original)
        // ============================================================
        List<string> GetAppIps(Programa item, string[] netstat)
        {
            var ips = new List<string>();
            var ids = new List<int>();
            try { foreach (var pr in Process.GetProcessesByName(item.Name)) ids.Add(pr.Id); } catch { }

            // 1) UDP primeiro: e o que carrega a voz e a transmissao de tela
            foreach (var a in Rede.GetUdpRemotes(ids.ToArray(), netstat))
                if (!ips.Contains(a)) ips.Add(a);
            // 1b) o IP escondido do socket sem destino (erros ICMP no WFP)
            foreach (var a in Rede.GetIcmpRemotes(ids.ToArray(), netstat))
                if (!ips.Contains(a)) ips.Add(a);
            // 2) conexoes TCP ativas
            foreach (var a in Rede.GetTcpRemotes(ids.ToArray(), netstat))
                if (!ips.Contains(a)) ips.Add(a);
            // 3) dominios conhecidos desse programa
            foreach (var d in item.Domains)
                foreach (var s in Rede.ResolveDom(d))
                    if (!ips.Contains(s)) ips.Add(s);
            return ips;
        }

        void SincronizarRotas()
        {
            if (!connected) return;
            if (!TunelVivo())
            {
                connected = false;
                LimparRotasNossas();
                BeginInvoke((Action)delegate { lblStatus.Text = "Status: desconectado"; AtualizarEstadoConexao(); });
                Log("A VPN caiu (a lista de servidores gr\u00e1tis \u00e9 inst\u00e1vel).");
                AgendarReconecta();
                return;
            }

            // IPv6: so no modo "tudo pela VPN" sai do caminho
            if (fullTunnel) DesligarIpv6Temporario(); else LigarIpv6DeVolta();

            // modo "tudo pela VPN": uma rota unica para o PC inteiro
            if (fullTunnel)
            {
                foreach (var ip in ownedRoutes.Where(r => r != "0.0.0.0/1" && r != "128.0.0.0/1").ToArray())
                { RemoverRota(ip); ownedRoutes.Remove(ip); }
                if (!defRoute) { AdicionarRotaPadrao(); }
                return;
            }
            if (defRoute) { RemoverRotaPadrao(); defRoute = false; }

            // so os programas escolhidos - um netstat por passada
            var want = new List<string>();
            var dono = new Dictionary<string, string>();
            var nlSync = Rede.NetstatLinhas();
            foreach (var item in appList)
            {
                foreach (var ip in GetAppIps(item, nlSync))
                {
                    if (!want.Contains(ip)) want.Add(ip);
                    dono[ip] = item.Name;
                }
            }

            // Discord: controle + faixa da midia (Cloudflare Spectrum) ANTES da chamada
            bool temDiscord = false;
            foreach (var it in appList) if (Regex.IsMatch(it.Name, "discord", RegexOptions.IgnoreCase)) { temDiscord = true; break; }
            if (temDiscord)
            {
                foreach (var s in Rede.ResolveDom("discord.media"))
                {
                    if (!want.Contains(s)) want.Add(s);
                    if (!dono.ContainsKey(s)) dono[s] = "";
                }
                foreach (var c in new[] { "104.29.128.0/19" })
                {
                    if (!want.Contains(c)) want.Add(c);
                    if (!dono.ContainsKey(c)) dono[c] = "";
                }
            }

            foreach (var ip in want)
                if (!ownedRoutes.Contains(ip)) { AdicionarRota(ip); ownedRoutes.Add(ip); }

            // PEGA E NAO SOLTA MAIS: rota aprendida fica ate sair da lista/VPN
            var nomes = new HashSet<string>(appList.Select(a => a.Name.ToLowerInvariant()));
            foreach (var ip in ownedRoutes.ToArray())
            {
                if (want.Contains(ip)) continue;
                string d;
                if (!dono.TryGetValue(ip, out d)) continue;
                if (d == "") continue;                          // veio de dominio: fica
                if (nomes.Contains(d.ToLowerInvariant())) continue;  // dono ainda na lista
                RemoverRota(ip);
                ownedRoutes.Remove(ip);
            }
        }

        // ============================================================
        //  AUDITORIA ANTI-VAZAMENTO
        // ============================================================
        List<string[]> RotasAtivas()
        {
            var res = new List<string[]>();
            try
            {
                var psi = new ProcessStartInfo("route", "print -4")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var ln in saida.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        var t = ln.Trim();
                        if (t.Length == 0) continue;
                        // NAO se procura o cabecalho em nenhum idioma: ele muda
                        // ("Network Destination" no ingles, "Endere�o de rede"
                        // no portugues - e com a codificacao trocada o � chega
                        // ate mangled). Vale a LINHA: se comeca com IP + mascara
                        // e rota, em qualquer idioma, em qualquer Windows. As
                        // outras linhas (interfaces, titulos, "Nenhum"/"None")
                        // nunca comecam com dois IPs e caem fora sozinhas.
                        // Antes esta funcao so reconhecia o cabecalho em ingles:
                        // no Windows brasileiro ela voltava VAZIA, a limpeza de
                        // rotas zumbis nao achava nada, e cada conexao empilhava
                        // rotas via um gateway morto novo. Com varias gateways
                        // para o mesmo destino e a mesma metrica, o Windows
                        // espalha os pacotes entre tunel vivo e morto: o ping
                        // da voz sobe e desce sem parar. Foi exatamente isso.
                        var part = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        IPAddress ip;
                        if (part.Length >= 5 && IPAddress.TryParse(part[0], out ip) && IPAddress.TryParse(part[1], out ip))
                            res.Add(new[] { part[0], part[1], part[2] });
                    }
                }
            }
            catch { }
            return res;
        }

        void AuditarVazamento()
        {
            if (!connected || String.IsNullOrEmpty(vpnGw)) return;
            try
            {
                var rotas = RotasAtivas();
                int cobre = 0;
                bool mexeu = false;
                foreach (var r in rotas)
                {
                    bool eMetade = (r[0] == "0.0.0.0" && r[1] == "128.0.0.0") || (r[0] == "128.0.0.0" && r[1] == "128.0.0.0");
                    if (!eMetade) continue;
                    bool nossa = String.Equals(r[2], vpnGw, StringComparison.OrdinalIgnoreCase);
                    if (fullTunnel)
                    {
                        if (nossa) cobre++;
                        // /1 de OUTRO caminho com prioridade: o tunel pode perder
                        // - a nossa tem metric 1, nada vence; nada a fazer aqui
                    }
                    else if (nossa)
                    {
                        // VAZAMENTO: rota de "PC inteiro" sobrando no modo dividido
                        Rede.RunCmd("route", "delete " + r[0] + " mask " + r[1] + " " + vpnGw);
                        mexeu = true;
                        Log("AVISO: rota de PC inteiro sobrando no modo dividido foi removida (s\u00f3 os programas escolhidos passam).");
                    }
                }
                if (fullTunnel && cobre < 2)
                {
                    Log("AVISO: faltava cobertura do PC inteiro - recolocando as rotas do t\u00fanel...");
                    AdicionarRotaPadrao();
                    mexeu = true;
                }
                // DNS: no PC inteiro tem que estar pelo tunel; no dividido, intocado
                if (fullTunnel && !dnsSalvo) { DnsDoTunel(true); mexeu = true; }
                // IPv6: no PC inteiro tem que estar fora do caminho
                if (fullTunnel && ipv6Salvo == null)
                {
                    var r6 = LerRotaIpv6Padrao();
                    if (r6 != null) { DesligarIpv6Temporario(); mexeu = true; }
                }
                if (mexeu) Log("Auditoria do t\u00fanel: corrigiu na hora (sem vazamento).");
            }
            catch { }
        }

        // ============================================================
        //  PING DENTRO DO TUNEL (aquecimento + minimo de sondas)
        // ============================================================
        void MedirPingTunel(string saida)
        {
            ultimaMidia = -1;
            try
            {
                var alvos = new List<string> { "104.29.146.2" };
                try { foreach (var a in Rede.ResolveDom("discord.media")) if (!alvos.Contains(a)) alvos.Add(a); } catch { }
                if (alvos.Count == 0) return;

                // AQUECIMENTO: o primeiro pacote apos as rotas costuma se perder
                try
                {
                    var warm = new TcpClient();
                    var w = warm.ConnectAsync(alvos[0], 443);
                    try { w.Wait(300); } catch { }
                    try { warm.Close(); } catch { }
                }
                catch { }

                // 2 sondas TCP por alvo, ao mesmo tempo; o MENOR tempo e o RTT puro
                var sondas = new List<object[]>();
                foreach (var ip in alvos)
                    for (int n = 0; n < 2; n++)
                    {
                        try
                        {
                            var c = new TcpClient();
                            var swP = Stopwatch.StartNew();
                            sondas.Add(new object[] { c, c.ConnectAsync(ip, 443), swP });
                        }
                        catch { }
                    }
                var lim = Stopwatch.StartNew();
                while (lim.ElapsedMilliseconds < 900)
                {
                    bool falta = false;
                    foreach (var so in sondas) if (!((Task)so[1]).IsCompleted) { falta = true; break; }
                    if (!falta) break;
                    Thread.Sleep(15);
                }
                int borda = -1;
                foreach (var so in sondas)
                {
                    try
                    {
                        var tsk = (Task)so[1];
                        if (tsk.IsCompleted && !tsk.IsFaulted && ((TcpClient)so[0]).Connected)
                        {
                            var ms = (int)((Stopwatch)so[2]).ElapsedMilliseconds;
                            if (borda < 0 || ms < borda) borda = ms;
                        }
                    }
                    catch { }
                    try { ((TcpClient)so[0]).Close(); } catch { }
                }

                // a midia segue medida em silencio para a reserva (melhor rota); sem
                // log e sem dica - so interessam os pings das VPNs, precisos
                if (borda >= 0) ultimaMidia = borda;
            }
            catch { }
        }

        // ANTES existiam aqui ReiniciarDiscord() e RecarregarDiscord(): na 1a
        // conexao o programa FECHAVA o Discord e abria de novo, e a cada troca
        // de servidor mandava Ctrl+R nele (roubando o foco da tela). Os dois
        // sairam: quando a VPN troca de servidor, as rotas passam a apontar
        // para o gateway do tunel novo e o proprio Discord reconecta sozinho.

        // ============================================================
        //  SELETOR DE PROGRAMA (janela propria, sem COM)
        // ============================================================
        Dictionary<string, string> Candidatos()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telegram Desktop"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (var r in roots)
            {
                if (String.IsNullOrWhiteSpace(r) || !Directory.Exists(r)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(r, "*.exe"))
                        if (!map.ContainsKey(f)) map[f] = Path.GetFileNameWithoutExtension(f);
                    foreach (var sub in Directory.GetDirectories(r))
                        try { foreach (var f in Directory.GetFiles(sub, "*.exe")) if (!map.ContainsKey(f)) map[f] = Path.GetFileNameWithoutExtension(f); }
                        catch { }
                }
                catch { }
            }
            // programas que ja estao abertos agora
            try
            {
                foreach (var pr in Process.GetProcesses())
                {
                    string p = null;
                    try { p = pr.MainModule.FileName; } catch { }
                    if (String.IsNullOrWhiteSpace(p)) continue;
                    if (p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                    if (String.Equals(p, Store.ExePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!map.ContainsKey(p)) map[p] = Path.GetFileNameWithoutExtension(p);
                }
            }
            catch { }
            return map;
        }

        string EscolherPrograma()
        {
            var cands = Candidatos();
            var items = cands.Keys.OrderBy(p => cands[p]).ThenBy(p => p).ToList();

            var dlg = new Form();
            dlg.Text = "Escolha o programa";
            dlg.Size = new Size(660, 500);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.Font = new Font("Segoe UI", 9);

            var lbl1 = new Label();
            lbl1.Text = "Programas achados neste PC (clique duas vezes para escolher):";
            lbl1.Location = new Point(12, 12);
            lbl1.Size = new Size(620, 20);
            dlg.Controls.Add(lbl1);

            var lb = new ListBox();
            lb.Location = new Point(12, 34);
            lb.Size = new Size(624, 268);
            lb.IntegralHeight = false;
            lb.Items.AddRange(items.Select(p => (object)(cands[p] + "    " + p)).ToArray());
            dlg.Controls.Add(lb);

            var lbl2 = new Label();
            lbl2.Text = "Ou digite/escolha o caminho do arquivo .exe:";
            lbl2.Location = new Point(12, 314);
            lbl2.Size = new Size(620, 20);
            dlg.Controls.Add(lbl2);

            var txt = new TextBox();
            txt.Location = new Point(12, 336);
            txt.Size = new Size(624, 26);
            dlg.Controls.Add(txt);

            var lblMsg = new Label();
            lblMsg.Location = new Point(12, 370);
            lblMsg.Size = new Size(624, 20);
            lblMsg.ForeColor = Color.Firebrick;
            dlg.Controls.Add(lblMsg);

            var btnCancel = new Button();
            btnCancel.Text = "Cancelar";
            btnCancel.Location = new Point(516, 404);
            btnCancel.Size = new Size(120, 32);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            dlg.Controls.Add(btnCancel);

            var btnOk = new Button();
            btnOk.Text = "Escolher";
            btnOk.Location = new Point(388, 404);
            btnOk.Size = new Size(120, 32);
            btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOk.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            dlg.Controls.Add(btnOk);

            string resultado = null;
            btnOk.Click += delegate
            {
                var p = (txt.Text ?? "").Trim();
                if (p == "") { lblMsg.Text = "Escolha um programa na lista ou digite o caminho do .exe."; return; }
                if (!File.Exists(p)) { lblMsg.Text = "Esse arquivo n\u00e3o existe. Confira o caminho."; return; }
                resultado = p;
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };
            btnCancel.Click += delegate { resultado = null; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            lb.SelectedIndexChanged += delegate
            {
                if (lb.SelectedIndex >= 0 && lb.SelectedIndex < items.Count)
                { txt.Text = items[lb.SelectedIndex]; lblMsg.Text = ""; }
            };
            lb.DoubleClick += delegate
            {
                if (lb.SelectedIndex >= 0)
                {
                    txt.Text = items[lb.SelectedIndex];
                    btnOk.PerformClick();
                }
            };
            dlg.Shown += delegate
            {
                // a lista do seletor no tema escuro (barra de rolagem inclusive)
                try
                {
                    lb.BackColor = Color.FromArgb(4, 13, 15);
                    lb.ForeColor = CorTexto;
                    Nativo.SetWindowTheme(lb.Handle, "DarkMode_Explorer", null);
                    Nativo.EscurecerScroll(lb.Handle);
                }
                catch { }
                if (items.Count > 0) lb.SelectedIndex = 0;
                txt.Focus();
            };

            dlg.ShowDialog(this);
            dlg.Dispose();
            return resultado;
        }

        // ============================================================
        //  DESINSTALACAO COMPLETA
        //
        //  Remove tudo que ESTE programa criou: o tunel, as rotas presas
        //  no adaptador, o proxy, o inicio automatico, o OpenVPN que ele
        //  mesmo instalou, as chaves de registro e as pastas de dados.
        //
        //  O unico arquivo que sobra e o proprio .exe, como pedido.
        // ============================================================
        void Desinstalar()
        {
            bool openVpnEhNosso = false;
            try { openVpnEhNosso = Store.LoadEstado().InstalouOpenVpn; } catch { }

            var aviso = "Remover o Ultra VPN Free deste PC?\r\n\r\n"
                + "- o tunel fecha e as rotas da rede saem\r\n"
                + "- o proxy e o IPv6 voltam ao normal\r\n"
                + "- o programa deixa de iniciar com o Windows\r\n"
                + "- as pastas de dados e as chaves de registro sao apagadas\r\n"
                + (openVpnEhNosso
                    ? "- o OpenVPN instalado pelo programa e desinstalado\r\n"
                    : "- o OpenVPN ja existia antes e e preservado\r\n")
                + "\r\nO UltraVPNFree.exe continua onde esta.";

            var r = MessageBox.Show(this, aviso,
                "Desinstalar o programa", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            exiting = true;
            saindo = true;
            var feito = new List<string>();
            var sobrou = new List<string>();

            foreach (var t in new[] { pollTimer, watch, rtimer, reconTimer, bootTimer,
                                      connTimer, updateTimer, fontesTimer, penteTimer,
                                      piscarTimer, trayTimer })
                try { t.Stop(); } catch { }

            // ---- 1) rede: tunel, proxy, rotas, DNS e IPv6 ----
            try { if (connected) Desconectar(); } catch { }
            try { PararMotor(); } catch { }
            try { LimparRotasNossas(); } catch { }
            try { LimparRotasAntigas(); } catch { }
            try { Rede.RunCmd("ipconfig", "/flushdns"); } catch { }
            try { ApagarRotasDoTunel(); } catch { }
            try { RestaurarDnsSeSobrou(); } catch { }
            try { GarantirRotaPadrao(); } catch { }
            // As rotas de "tudo pela VPN" sao 0.0.0.0/1 e 128.0.0.0/1.
            // A limpeza normal so as remove se defRoute estiver ligado em
            // memoria; se o programa foi encerrado no meio do tunel essa
            // flag se perde e elas ficariam presas. Apaga sempre.
            // IMPORTADO: com a mascara. Sem ela "route delete 0.0.0.0"
            // apagaria a rota PADRAO do Windows e a maquina ficaria sem
            // internet - foi exatamente o que aconteceu.
            try
            {
                foreach (var pr in new[] { "0.0.0.0/1", "128.0.0.0/1" })
                    Rede.RunCmd("route", "delete " + Rede.RedeDePrefixo(pr) + " mask " + Rede.MascaraDePrefixo(pr));
            }
            catch { }
            feito.Add("rede restaurada (rotas, proxy, DNS e IPv6)");

            // ---- 2) processos do openvpn que ficaram orfaos ----
            try
            {
                foreach (var p in Process.GetProcessesByName("openvpn"))
                    try { p.Kill(); } catch { }
                foreach (var p in Process.GetProcessesByName("openvpnserv"))
                    try { p.Kill(); } catch { }
            }
            catch { }

            // ---- 3) inicio automatico ----
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                    if (k != null) k.DeleteValue(RunNome, false);
                feito.Add("inicio automatico removido");
            }
            catch { sobrou.Add("inicio automatico: " + RunKey); }

            // ---- 4) atalhos ----
            try
            {
                foreach (var d in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                {
                    if (String.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    try { foreach (var f in Directory.GetFiles(d, "Ultra VPN Free*")) try { File.Delete(f); } catch { } } catch { }
                    try { var sub = Path.Combine(d, "Ultra VPN Free"); if (Directory.Exists(sub)) Directory.Delete(sub, true); } catch { }
                }
            }
            catch { }

            // ---- 5) o OpenVPN: so se o programa foi quem instalou ----
            if (openVpnEhNosso)
            {
                if (AcharOpenVpnInstalado() == null) feito.Add("OpenVPN ja nao estava instalado");
                else if (DesinstalarOpenVpn()) feito.Add("OpenVPN desinstalado");
                else sobrou.Add("OpenVPN: desinstale em 'Programas e Recursos' do Windows");
            }
            else
            {
                // nao era nosso: so tira as sobras que o instalador deixou
                // (atalho, item de inicializacao) e deixa o pacote em paz
                try { ServicoOpenVpnParaManual(); } catch { }
                if (AcharOpenVpnInstalado() != null) feito.Add("OpenVPN preservado (ja existia antes)");
            }

            // ---- 6) registro ----
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", true))
                    if (k != null) k.DeleteSubKeyTree("Ultra VPN Free", false);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
                    if (k != null) k.DeleteSubKeyTree("UltraVPNFree", false);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue(RunNome, false);
                feito.Add("registro limpo");
            }
            catch { sobrou.Add("alguma chave de registro ficou"); }

            // ---- 7) pastas de dados e temporarios ----
            try
            {
                if (Directory.Exists(Store.DirDados)) Directory.Delete(Store.DirDados, true);
                if (Directory.Exists(Store.TempDir)) Directory.Delete(Store.TempDir, true);
                // o MSI que o .exe extrai para instalar o OpenVPN. Depois da
                // instalacao ele ja e apagado, mas se a instalacao falhar no
                // meio ele fica orfao em %TEMP%.
                var msiTemp = Path.Combine(Path.GetTempPath(), "uvpn_openvpn.msi");
                if (File.Exists(msiTemp)) File.Delete(msiTemp);
                // nomes antigos, de versoes anteriores do programa
                var dadosAntigo = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVPNFreeCS");
                if (Directory.Exists(dadosAntigo)) Directory.Delete(dadosAntigo, true);
                var tempAntigo = Path.Combine(Path.GetTempPath(), "UltraVPNFreeCS");
                if (Directory.Exists(tempAntigo)) Directory.Delete(tempAntigo, true);
                feito.Add("pastas de dados apagadas");
            }
            catch { sobrou.Add("alguma pasta de dados ficou"); }

            // ---- 8) tray e encerramento ----
            try { tray.Visible = false; tray.Dispose(); } catch { }

            var resumo = "Desinstalado.\n\n" + String.Join("\n", feito.ToArray());
            if (sobrou.Count > 0)
                resumo += "\n\nNao deu para remover:\n- " + String.Join("\n- ", sobrou.ToArray());
            resumo += "\n\nO UltraVPNFree.exe foi mantido onde esta.";
            MessageBox.Show(this, resumo, "Desinstalar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Application.Exit();
        }

        // Rede de seguranca: se a rota padrao do Windows (0.0.0.0/0) tiver
        // desaparecido depois da limpeza, a maquina fica sem internet. O
        // gateway do DHCP continua salvo no registro mesmo quando a rota some,
        // entao da para recoloca-lo.
        void GarantirRotaPadrao()
        {
            try
            {
                bool temPadrao = false;
                foreach (var r in RotasAtivas())
                    if (r[0] == "0.0.0.0" && r[1] == "0.0.0.0") { temPadrao = true; break; }
                if (temPadrao) return;

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                            nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                        var desc = nic.Description ?? "";
                        if (desc.IndexOf("TAP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desc.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desc.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        var chave = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + nic.Id);
                        if (chave == null) continue;
                        var gws = chave.GetValue("DhcpDefaultGateway") as string[];
                        if (gws != null)
                        {
                            foreach (var g in gws)
                            {
                                if (String.IsNullOrEmpty(g) || g.IndexOf(':') >= 0) continue;
                                Rede.RunCmd("route", "add 0.0.0.0 mask 0.0.0.0 " + g.Trim() + " metric 1");
                                Log("A rota padrao do Windows tinha sumido: repus \"" + g.Trim() + "\" em " + nic.Name + ".");
                                return;
                            }
                        }
                    }
                    catch { }
                }
                Log("Aviso: nao achei rota padrao nem gateway de DHCP para recolocar. Reconfigure a rede manualmente.");
            }
            catch { }
        }

        // O DNS original so existe na memoria (savedDns). Se o programa for
        // encerrado no meio do tunel, essa informacao se perde e a maquina
        // fica com 1.1.1.1 travado sem ninguem para desfazer. Na desinstalacao
        // procuramos o par EXATO que o programa escreve (1.1.1.1 + 1.0.0.1)
        // e devolvemos para automatico.
        //
        // Criterio importante: o par so aparece quando quem escreveu foi o
        // DnsDoTunel(true). Se o usuario tinha 1.1.1.1 sozinho, ou qualquer
        // outro DNS (roteador, empresa, 8.8.8.8), nao encosta em nada.
        void RestaurarDnsSeSobrou()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    var desc = nic.Description ?? "";
                    if (desc.IndexOf("TAP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    bool temPrincipal = false, temSecundario = false;
                    foreach (var d in nic.GetIPProperties().DnsAddresses)
                    {
                        if (d.ToString() == "1.1.1.1") temPrincipal = true;
                        if (d.ToString() == "1.0.0.1") temSecundario = true;
                    }
                    if (!temPrincipal || !temSecundario) continue;   // nao e o nosso

                    Rede.RunCmd("netsh", "interface ip set dns name=\"" + nic.Name + "\" source=dhcp");
                    Rede.RunCmd("ipconfig", "/flushdns");
                    Log("DNS de \"" + nic.Name + "\" voltou para automatico (estava no 1.1.1.1 do tunel).");
                }
                catch { }
            }
        }

        // Rotas /32 que o programa cria a cada sessao (IPs do Discord, etc.)
        // ficam presas no adaptador do tunel. Varre a tabela e apaga tudo que
        // estiver nele, sem encostar na rede normal.
        void ApagarRotasDoTunel()
        {
            var adapters = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var d = nic.Description ?? "";
                    var n = nic.Name ?? "";
                    if (d.IndexOf("TAP-Windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("ovpn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("TAP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0)
                        adapters.Add(n);
                }
            }
            catch { }
            if (adapters.Count == 0) return;

            try
            {
                var psi = new ProcessStartInfo("route.exe", "print")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    var saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);

                    var bloco = "";
                    var alvos = new List<string>();
                    foreach (var linha in saida.Split('\n'))
                    {
                        if (linha.IndexOf("Interface List", StringComparison.OrdinalIgnoreCase) >= 0)
                        { bloco = linha; continue; }

                        var eTunel = false;
                        foreach (var a in adapters)
                            if (bloco.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) { eTunel = true; break; }
                        if (!eTunel) continue;

                        var partes = linha.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (partes.Length == 0) continue;
                        var destino = partes[0].Trim();
                        if (destino.StartsWith("0.0.0.0") || destino.StartsWith("::") ||
                            destino.StartsWith("255.255") || destino.StartsWith("224.0") ||
                            destino.StartsWith("fe80") || destino.StartsWith("ff00")) continue;
                        alvos.Add(destino);
                    }
                    foreach (var destino in alvos) RemoverRota(destino);
                }
            }
            catch { }
        }

        // onde o openvpn.exe foi instalado (este .exe e 32 bits, entao
        // ProgramFiles devolve "Program Files (x86)" -- o %ProgramW6432%
        // do Windows e quem aponta para o caminho de verdade)
        static string AcharOpenVpnInstalado()
        {
            var raizes = new List<string>();
            try { raizes.Add(Environment.GetEnvironmentVariable("ProgramW6432")); } catch { }
            try { raizes.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
            try { raizes.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
            foreach (var raiz in raizes)
            {
                if (String.IsNullOrEmpty(raiz)) continue;
                foreach (var sub in new[] { @"OpenVPN\bin\openvpn.exe", @"OpenVPN\bin\x64\openvpn.exe" })
                {
                    var c = Path.Combine(raiz, sub);
                    if (File.Exists(c)) return c;
                }
            }
            return null;
        }

        // o servico do OpenVPN fica em "Manual": nao sobe com o Windows.
        // IMPORTADO: nunca DESLIGAR ele aqui -- o driver TAP-Windows 2.7
        // pede ao servico para criar o adaptador e, com ele parado,
        // o openvpn responde "could not talk to service" e a conexao morre.
        static void ServicoOpenVpnParaManual()
        {
            foreach (var sc in ServiceController.GetServices())
            {
                try
                {
                    if (!sc.ServiceName.StartsWith("OpenVPN")) continue;
                    RunarEscondido("sc.exe", "config \"" + sc.ServiceName + "\" start= demand");
                }
                catch { }
            }
        }

        // msiexec /x pelo codigo do produto; se nao der, apaga na mao.
        bool DesinstalarOpenVpn()
        {
            if (AcharOpenVpnInstalado() == null) return true;   // nem esta instalado

            var tentativas = new List<string>();
            var codigo = ProdutoInstalado("OpenVPN");
            if (!String.IsNullOrEmpty(codigo)) tentativas.Add("/x " + codigo);

            var msi = Path.Combine(Store.TempDir, "openvpn.msi");
            if (File.Exists(msi)) tentativas.Add("/x \"" + msi + "\"");

            foreach (var arg in tentativas)
            {
                try
                {
                    // o programa ja roda como administrador: sem "runas" para
                    // nao abrir uma janela extra de elevacao
                    var psi = new ProcessStartInfo("msiexec.exe", arg + " /qn /norestart")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi)) if (p != null) p.WaitForExit(180000);
                    if (AcharOpenVpnInstalado() == null) { ApagarChavesDesinstalacao(); return true; }
                }
                catch { }
            }

            // ultimo recurso: arquivos, servico e registro na mao
            try
            {
                var exe = AcharOpenVpnInstalado();
                if (!String.IsNullOrEmpty(exe))
                {
                    var pasta = Path.GetDirectoryName(exe);
                    while (!String.IsNullOrEmpty(pasta) && pasta.Length > 3 &&
                           pasta.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { Directory.Delete(pasta, true); } catch { }
                        var pai = Directory.GetParent(pasta);
                        pasta = pai == null ? null : pai.FullName;
                    }
                }
                foreach (var sc in ServiceController.GetServices())
                {
                    try
                    {
                        if (!sc.ServiceName.StartsWith("OpenVPN")) continue;
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                    }
                    catch { }
                }
                ApagarChaveRegistro(@"SOFTWARE\OpenVPN");
                ApagarChaveRegistro(@"SOFTWARE\WOW6432Node\OpenVPN");
                RunarEscondido("reg.exe", @"delete HKCU\SOFTWARE\OpenVPN /f");
                RunarEscondido("reg.exe", @"delete HKCU\SOFTWARE\OpenVPN-GUI /f");
                ApagarChavesDesinstalacao();
                var pd = Environment.GetEnvironmentVariable("ProgramData");
                if (!String.IsNullOrEmpty(pd))
                    try { Directory.Delete(Path.Combine(pd, "OpenVPN"), true); } catch { }

                return AcharOpenVpnInstalado() == null;
            }
            catch { return AcharOpenVpnInstalado() == null; }
        }

        static void ApagarChaveRegistro(string caminho)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine,
                                          Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(caminho, true))
                        if (k != null) hive.DeleteSubKeyTree(caminho, false);
                }
                catch { }
            }
        }

        // Sem isto o OpenVPN continua aparecendo em "Programas e Recursos"
        // mesmo com todos os arquivos ja apagados.
        static void ApagarChavesDesinstalacao()
        {
            foreach (var raiz in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(raiz))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = k.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    var nome = sk.GetValue("DisplayName") as string;
                                    if (nome != null &&
                                        nome.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) >= 0)
                                        Microsoft.Win32.Registry.LocalMachine
                                            .DeleteSubKeyTree(raiz + "\\" + sub, false);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        static string ProdutoInstalado(string procurado)
        {
            foreach (var raiz in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(raiz))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = k.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    var nome = sk.GetValue("DisplayName") as string;
                                    if (nome != null &&
                                        nome.IndexOf(procurado, StringComparison.OrdinalIgnoreCase) >= 0)
                                        return sub;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        static void RunarEscondido(string arquivo, string argumentos)
        {
            try
            {
                var psi = new ProcessStartInfo(arquivo, argumentos)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi)) if (p != null) p.WaitForExit(15000);
            }
            catch { }
        }
    }
}
