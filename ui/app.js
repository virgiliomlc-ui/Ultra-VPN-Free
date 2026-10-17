/* Ultra VPN Free - ponte entre o C# e a tela.
   O C# manda mensagem por PostWebMessageAsJson; aqui vira tela.
   Tudo que o usuario clica volta por chrome.webview.postMessage({...}). */

var ET_QUADROS = 12;          // quadros do et (o C# manda a duracao)
var ET_MS = 220;              // 220 ms por quadro, como no WinForms

var estado = {
  servidores: [],
  programas: [],
  pais: [],
  tema: 'ets',
  conectado: false,
  ip: '',
  servidorAtual: '',
  fullTunnel: false,
  autoRecon: false,
  startup: false,
  bootConn: false,
  protocolo: 'auto'
};

var srvSel = -1;   // indice do servidor escolhido
var progSel = -1;

/* ---------------- ponte com o C# ---------------- */
// Qualquer erro de JavaScript e devolvido para o programa: sem isso o
// erro some em silencio e nao ha como saber o que deu.
window.onerror = function (msg, url, linha, col) {
  manda('errojs', { texto: String(msg) + ' @ ' + linha + ':' + col });
  return false;
};
window.addEventListener('unhandledrejection', function (ev) {
  manda('errojs', { texto: 'promessa rejeitada: ' + String(ev.reason) });
});

function manda(acao, dados) {
  var m = { acao: acao };
  if (dados) for (var k in dados) m[k] = dados[k];
  try { chrome.webview.postMessage(m); } catch (e) { }
}

/* O programa entrega os dados CHAMANDO esta funcao (ExecuteScriptAsync).
   Antes isso vinha por PostWebMessageAsJson + evento "message", e o evento
   nunca chegava nesta tela - o postMessage do JS para o C# funcionava, mas
   o caminho inverso nao. Chamar a funcao direto nao tem esse problema. */
window.uvfn_recebe = function (d) {
  try {
    if (!d) return;
    switch (d.tipo) {
      case 'estado':   aplicarEstado(d); break;
      case 'servidores': aplicarServidores(d); break;
      case 'programas': aplicarProgramas(d); break;
      case 'log':      addLog(d.texto); break;
      case 'ip':       setIp(d.ip); break;
      case 'status':   setConectado(d.conectado, d.servidor, d.endereco); break;
      case 'paises':   aplicarPaises(d.paises); break;
      case 'temas':    aplicarTemas(d.temas); break;
      case 'et':       mostrarEt(d.quadros); break;
      case 'imagens':  aplicarImagens(d); break;
    }
  } catch (e) {
    manda('errojs', { texto: 'uvfn_recebe: ' + e.message });
  }
};

/* ---------------- fundo, imagens e temas ---------------- */
function aplicarImagens(d) {
  estado.imagens = estado.imagens || {};
  if (d.fundo) estado.imagens.fundo = d.fundo;
  if (d.goku) estado.imagens.goku = d.goku;
  if (d.logo) document.getElementById('logo').src = d.logo;
  aplicarTema(estado.tema);
}

/* Os 3 temas, os mesmos do programa antigo (ver MontarFundo no C#):
     ets    -> a foto do alien, cobrindo a janela
     escuro -> degrade de rgb(12,24,28) ate rgb(4,10,12), sem foto
     goku   -> a foto do Goku com um veu preto por cima (105/255) */
function aplicarTema(chave) {
  estado.tema = chave || 'ets';
  var c = document.body;
  var img = estado.imagens || {};
  if (estado.tema === 'goku' && img.goku) {
    c.style.backgroundImage = 'url("' + img.goku + '")';
    c.className = 'tema-goku';
  } else if (estado.tema === 'escuro') {
    // VAZIO, e nao 'none': um background-image posto aqui no style tem
    // prioridade sobre a classe, e 'none' apagava o degrade do CSS - o
    // tema escuro continuava mostrando a foto do alien.
    c.style.backgroundImage = '';
    c.className = 'tema-escuro';
  } else {
    c.style.backgroundImage = img.fundo ? 'url("' + img.fundo + '")' : '';
    c.className = 'tema-ets';
  }
  var s = document.getElementById('tema');
  if (s && s.value !== estado.tema) s.value = estado.tema;
}

function aplicarTemas(lista) {
  estado.temasLista = lista || [];
  var s = document.getElementById('tema');
  if (!s) return;
  s.innerHTML = '';
  for (var i = 0; i < estado.temasLista.length; i++) {
    var o = document.createElement('option');
    o.value = estado.temasLista[i].chave;
    o.textContent = estado.temasLista[i].nome;
    s.appendChild(o);
  }
  s.value = estado.tema;
}

/* ---------------- pais / tema / tunel ---------------- */
function aplicarPaises(lista) {
  estado.pais = lista || [];
  var s = document.getElementById('pais');
  var atual = s.value;
  s.innerHTML = '';
  // a primeira opcao e a regiao inteira: e o que o programa antigo mostra
  var t = document.createElement('option');
  t.value = '';
  t.textContent = 'Todos os países da América do Sul';
  s.appendChild(t);
  for (var i = 0; i < estado.pais.length; i++) {
    var o = document.createElement('option');
    o.value = estado.pais[i].iso;
    o.textContent = estado.pais[i].nome;
    s.appendChild(o);
  }
  s.value = atual;
}

function aplicarEstado(d) {
  for (var k in d) if (k !== 'tipo') estado[k] = d[k];
  document.getElementById('ckTudo').checked   = !!estado.fullTunnel;
  document.getElementById('ckAuto').checked   = !!estado.autoRecon;
  document.getElementById('ckBoot').checked   = !!estado.startup;
  document.getElementById('ckBootConn').checked = !!estado.bootConn;
  if (estado.protocolo) document.getElementById('tunel').value = estado.protocolo;
  if (estado.tema) aplicarTema(estado.tema);
  if (estado.ip) setIp(estado.ip);
  if (typeof estado.conectado !== 'undefined') setConectado(estado.conectado, estado.servidorAtual, estado.servidorEndereco);
}

function setIp(ip) {
  estado.ip = ip || '';
  document.getElementById('ipAtual').textContent = 'IP atual: ' + (ip || '...');
}

/* ---------------- lista de servidores ---------------- */
function sparkline(pct) {
  // mesmo desenho do WinForms: barras ciano + altura pela velocidade
  var n = 14, h = 18, w = 86, bw = 4, gap = 2;
  var svg = '<svg class="spark" width="' + w + '" height="' + h + '">';
  var med = Math.max(0.08, Math.min(1, pct));
  for (var i = 0; i < n; i++) {
    // onda deterministica: mesma forma para o mesmo indice
    var onda = 0.55 + 0.45 * Math.sin(i * 1.7);
    var alt = Math.max(2, Math.round(h * med * onda));
    var x = i * (bw + gap);
    svg += '<rect x="' + x + '" y="' + (h - alt) + '" width="' + bw +
           '" height="' + alt + '" fill="rgba(0,216,222,.85)"/>';
  }
  return svg + '</svg>';
}

/* ---------------- ordenar a tabela ----------------
   Mesmo desenho do ListView do WinForms: clicar no cabecalho ordena por
   aquela coluna e clicar de novo inverte. O programa faz a ordenacao de
   verdade (ele que sabe o ping medido e a velocidade); a tela so avisa e
   desenha a seta. */
function marcarColunaOrdenada() {
  var ths = document.querySelectorAll('#lista thead th');
  for (var i = 0; i < ths.length; i++) {
    var on = (i === estado.ordemCol);
    ths[i].className = on ? 'ord' : '';
    var s = ths[i].querySelector('.seta');
    if (s) s.textContent = on ? (estado.ordemCresc ? ' ▲' : ' ▼') : '';
  }
}

document.addEventListener('click', function (ev) {
  var th = ev.target.closest ? ev.target.closest('#lista thead th') : null;
  if (!th) return;
  ev.preventDefault();
  manda('mudarOrdem', { col: parseInt(th.getAttribute('data-col'), 10) });
});

/* ---------------- filtro da tabela ----------------
   O programa manda a lista TODA e quem filtra e a tela, para o clique ser
   instantaneo:
     - pais em "Todos os paises" -> todos; escolhendo um, so aquele
     - tunel em "UDP e TCP (automatico)" -> todos; escolhendo UDP, so UDP
   O filtro do tunel espelha o do programa antigo: servidor que fala "os
   dois" aparece nas duas escolhas; servidor que fala so um so aparece
   quando esse e o escolhido. */
function filtroPais() {
  var el = document.getElementById('pais');
  return el ? (el.value || '') : '';
}
function filtroTunel() {
  var el = document.getElementById('tunel');
  return el ? (el.value || 'auto') : 'auto';
}
function servidoresVisiveis() {
  var iso = filtroPais();
  var tun = filtroTunel().toUpperCase();
  var fora = [];
  for (var i = 0; i < estado.servidores.length; i++) {
    var s = estado.servidores[i];
    var passa = true;
    if (iso && String(s.iso || '') !== iso) passa = false;
    if (passa && (tun === 'UDP' || tun === 'TCP')) {
      var p = String(s.proto || '').toUpperCase();
      if (p && p !== 'BOTH' && p !== 'AUTO' && p !== tun) passa = false;
    }
    if (passa) fora.push(i);
  }
  return fora;
}

function aplicarServidores(d) {
  estado.servidores = d.lista || [];
  estado.ordemCol = (typeof d.ordemCol === 'undefined' || d.ordemCol === null) ? -1 : d.ordemCol;
  estado.ordemCresc = !!d.ordemCresc;
  marcarColunaOrdenada();
  var idxs = servidoresVisiveis();
  var total = idxs.length;
  mostrarLinhaConta(total);

  var velocidadeMax = 1;
  for (var j = 0; j < idxs.length; j++) {
    if (estado.servidores[idxs[j]].vel > velocidadeMax) velocidadeMax = estado.servidores[idxs[j]].vel;
  }

  var html = '';
  // a linha de grupo do mockup: o nome da regiao uma vez, e os servidores
  // embaixo com o nome completo ("Argentina - Olivos")
  if (total > 0) {
    html += '<tr class="grupo"><td>América do Sul</td><td></td><td></td><td></td><td></td></tr>';
  }
  for (var j = 0; j < idxs.length; j++) {
    var i = idxs[j];
    var s = estado.servidores[i];
    var nome = String(s.pais || '');
    var cid = String(s.cidade || '').trim();
    if (cid && nome.indexOf(cid) < 0) nome = nome + ' - ' + cid;
    var pct = velocidadeMax > 0 ? (s.vel / velocidadeMax) : 0;
    var mbps = (s.vel / 1048576);
    var txtVel = (mbps >= 0.05 ? mbps.toFixed(1).replace('.', ',') : '0') + ' Mbps';
    html += '<tr class="srv' + (i === srvSel ? ' sel' : '') + '" data-i="' + i + '">' +
      '<td>' + esc(nome) + '</td>' +
      '<td>' + esc(s.ip) + (s.porta ? ':' + s.porta : '') + '</td>' +
      '<td' + (s.ping >= 0 ? '' : ' class="semMedida"') + '>' + (s.ping >= 0 ? (s.ping + ' ms') : '-') + '</td>' +
      '<td>' + sparkline(pct) + ' <span class="mbps' + (mbps < 0.05 ? ' zero' : '') + '">' + txtVel + '</span></td>' +
      '<td>' + esc(s.proto) + '</td>' +
      '</tr>';
  }
  document.getElementById('corpo').innerHTML = html;

  var linhas = document.querySelectorAll('#corpo tr.srv');
  for (var k = 0; k < linhas.length; k++) {
    (function (idx, el) {
      el.addEventListener('click', function () {
        selServidor(idx, el);
      });
      // DUPLO CLIQUE = a troca rapida do programa antigo (lv.DoubleClick):
      // escolhe o servidor da linha e liga; se ja estiver ligado, desliga
      // e liga no outro
      el.addEventListener('dblclick', function () {
        selServidor(idx, el);
        manda('conectarDireto', { indice: idx, ip: estado.servidores[idx].ip });
      });
    })(parseInt(linhas[k].getAttribute('data-i'), 10), linhas[k]);
  }
  medirTrilho('cxLista', 'polegarLista', total);
}

// so redesenha a tabela, sem esperar o programa mandar nada
function redesenharTabela() {
  aplicarServidores({ lista: estado.servidores, ordemCol: estado.ordemCol, ordemCresc: estado.ordemCresc });
}

function selServidor(idx, el) {
  var antes = document.querySelector('#corpo tr.srv.sel');
  if (antes) antes.classList.remove('sel');
  el.classList.add('sel');
  srvSel = idx;
  var s = estado.servidores[idx];
  manda('escolherServidor', { indice: idx, ip: s.ip, porta: s.porta });
}

function esc(t) {
  return String(t == null ? '' : t)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/* ---------------- lista de programas ---------------- */
function aplicarProgramas(d) {
  estado.programas = d.lista || [];
  var h = '';
  for (var i = 0; i < estado.programas.length; i++) {
    var p = estado.programas[i];
    h += '<div class="prog' + (i === progSel ? ' sel' : '') + '" data-i="' + i + '">' +
         '<img alt="">' +
         '<span>' + esc(p.nome) + '</span></div>';
  }
  document.getElementById('listaProg').innerHTML = h;
  var itens = document.querySelectorAll('#listaProg .prog');
  for (var k = 0; k < itens.length; k++) {
    (function (idx, el) {
      el.addEventListener('click', function () {
        var ant = document.querySelector('#listaProg .prog.sel');
        if (ant) ant.classList.remove('sel');
        el.classList.add('sel');
        progSel = idx;
        manda('escolherPrograma', { indice: idx });
      });
    })(parseInt(itens[k].getAttribute('data-i'), 10), itens[k]);
  }
  // o icone do lado do nome e o ET que pisca (o mesmo da barra vertical),
  // com fundo preto - como no desenho. O mesmo quadro vai para as duas
  // caixas na mesma hora, entao os dois piscam juntos.
  var icones = document.querySelectorAll('#listaProg .prog img');
  for (var j = 0; j < icones.length; j++) {
    if (estado.etQuadros && estado.etQuadros.length) {
      icones[j].src = estado.etQuadros[quadroEt()];
    } else if (estado.imagens && estado.imagens.et) {
      icones[j].src = estado.imagens.et;
    }
  }
  medirTrilho('cxProg', 'polegarProg', estado.programas.length);
}

/* ---------------- log ---------------- */
function addLog(texto) {
  var cx = document.getElementById('logRolagem');
  var div = document.createElement('div');
  div.className = 'linhaLog';
  div.textContent = texto;
  cx.appendChild(div);
  while (cx.childNodes.length > 400) cx.removeChild(cx.firstChild);
  cx.scrollTop = cx.scrollHeight;
  medirTrilho('log', 'polegarLog', cx.childNodes.length);
}

/* ---------------- o ET que pisca ---------------- */
/* o ET e o proprio polegar da barra vertical. Medidas do C# original:
   trilho = 17 px, ET = 17 x 17, centralizado no polegar.
   Se a caixa nao rola, some o trilho inteiro (como no WinForms). */
function medirTrilho(idCx, idPol, itens) {
  var cx = document.getElementById(idCx);
  var rol = cx.querySelector('.rolagem');
  var pol = document.getElementById(idPol);
  var precisa = rol.scrollHeight > rol.clientHeight + 1;
  cx.classList.toggle('semRolagem', !precisa);
  if (!precisa) { pol.innerHTML = ''; return; }

  var pista = rol.clientHeight - 4;
  var frac = Math.min(1, rol.clientHeight / Math.max(1, rol.scrollHeight));
  var altPolegar = Math.max(26, Math.round(pista * frac));
  var topo = 2 + Math.round((pista - altPolegar) * fracRolagem(rol));

  pol.style.top = topo + 'px';
  pol.style.height = altPolegar + 'px';
  pol.style.display = 'flex';
  if (!pol.querySelector('img')) pol.innerHTML = '<img alt="">';
  var img = pol.querySelector('img');
  if (estado.etQuadros && estado.etQuadros.length) {
    // o C# manda os quadros ja sem o preto de fundo
    var f = estado.etQuadros[Math.floor(Date.now() / ET_MS) % estado.etQuadros.length];
    img.src = f;
  }
}

function fracRolagem(rol) {
  if (rol.scrollHeight <= rol.clientHeight) return 0;
  var max = rol.scrollHeight - rol.clientHeight;
  return Math.max(0, Math.min(1, rol.scrollTop / max));
}

/* o ET pisca: o mesmo relogio de 220 ms do WinForms. Um unico relogio
   para o ET da barra E para o do lado do nome do programa, para os dois
   piscarem sempre no mesmo quadro. */
var ET_T0 = Date.now();
function quadroEt() {
  if (!estado.etQuadros || !estado.etQuadros.length) return 0;
  return Math.floor((Date.now() - ET_T0) / ET_MS) % estado.etQuadros.length;
}
var etTimer = null;
function mostrarEt(quadros) {
  estado.etQuadros = quadros || [];
  if (!estado.etQuadros.length) return;
  if (etTimer) clearInterval(etTimer);
  var pisca = function () {
    var q = estado.etQuadros;
    if (!q.length) return;
    var f = q[quadroEt()];
    var barras = document.querySelectorAll('.cx:not(.semRolagem) .polegar img');
    for (var i = 0; i < barras.length; i++) barras[i].src = f;
    var icones = document.querySelectorAll('#listaProg .prog img');
    for (var j = 0; j < icones.length; j++) icones[j].src = f;
  };
  pisca();
  etTimer = setInterval(pisca, ET_MS);
}

/* ---------------- status ---------------- */
function setConectado(conectado, servidor, endereco) {
  estado.conectado = !!conectado;
  if (servidor) estado.servidorAtual = servidor;
  if (endereco) estado.servidorEndereco = endereco;
  var b = document.getElementById('conectar');
  var txt = b.querySelector('.txt');
  var info = document.getElementById('infoConectar');
  if (txt) txt.textContent = estado.conectado ? 'DESCONECTAR' : 'CONECTAR';
  b.classList.toggle('conectado', estado.conectado);
  // o nome do servidor com o IP e a porta vao DENTRO do botao, logo abaixo
  // do "DESCONECTAR" - e nao na linha de cima, que e da contagem
  if (info) {
    if (estado.conectado && estado.servidorAtual) {
      var t = estado.servidorAtual;
      if (estado.servidorEndereco) t += '  ·  ' + estado.servidorEndereco;
      info.textContent = t;
    } else info.textContent = '';
  }
  mostrarLinhaConta();
}

// a linha de cima mostra quantos servidores ha. Quem escreve nela e esta
// funcao, para o botao e a lista nunca brigarem pelo mesmo lugar.
function mostrarLinhaConta(quantos) {
  var linha = document.getElementById('conta');
  if (!linha) return;
  var t = (typeof quantos === 'number') ? quantos : (estado.servidores || []).length;
  if (!t) {
    // filtro que nao achou nada: diz o que happened em vez de sumir
    linha.textContent = (estado.servidores || []).length
      ? 'nenhum servidor com esse filtro'
      : 'carregando servidores...';
    return;
  }
  linha.textContent = t + (t === 1 ? ' servidor na América do Sul | 1 mostrado'
                                   : ' servidores na América do Sul | ' + t + ' mostrados');
}

/* ---------------- botoes ---------------- */
function ligar(id, acao) {
  var el = document.getElementById(id);
  if (el) el.addEventListener('click', function () { manda(acao); });
}

ligar('btnAtualizar', 'atualizar');
ligar('btnPing', 'medirPing');
ligar('btnMin', 'minimizar');
ligar('btnMax', 'maximizar');
ligar('btnFechar', 'fechar');
ligar('conectar', 'conectarDesconectar');
ligar('btnAddProg', 'adicionarPrograma');
ligar('btnRemoveProg', 'removerPrograma');
ligar('btnDesinst', 'desinstalar');

document.getElementById('pais').addEventListener('change', function () {
  // a tabela filtra NA HORA; o programa so precisa saber para a conexao
  redesenharTabela();
  manda('mudarPais', { iso: this.value });
});
document.getElementById('tunel').addEventListener('change', function () {
  redesenharTabela();
  manda('mudarProtocolo', { protocolo: this.value });
});
document.getElementById('tema').addEventListener('change', function () {
  aplicarTema(this.value);
  manda('mudarTema', { tema: this.value });
});

var checks = { 'ckTudo': 'fullTunnel', 'ckAuto': 'autoRecon', 'ckBoot': 'startup', 'ckBootConn': 'bootConn' };
for (var cid in checks) {
  (function (id, campo) {
    document.getElementById(id).addEventListener('change', function () {
      var o = {}; o[campo] = this.checked;
      manda('mudarOpcao', o);
    });
  })(cid, checks[cid]);
}

/* rolagem: o ET acompanha o movimento */
document.addEventListener('scroll', function (ev) {
  var cx = ev.target.closest ? ev.target.closest('.cx') : null;
  if (!cx) return;
  var rol = cx.querySelector('.rolagem');
  if (rol !== ev.target) return;
  var pol = cx.querySelector('.polegar');
  if (!pol) return;
  var pista = rol.clientHeight - 4;
  var frac = Math.min(1, rol.clientHeight / Math.max(1, rol.scrollHeight));
  var altPolegar = Math.max(26, Math.round(pista * frac));
  pol.style.top = (2 + Math.round((pista - altPolegar) * fracRolagem(rol))) + 'px';
}, true);

window.addEventListener('resize', function () {
  medirTrilho('cxLista', 'polegarLista', estado.servidores.length);
  medirTrilho('cxProg', 'polegarProg', estado.programas.length);
  medirTrilho('log', 'polegarLog', document.getElementById('logRolagem').childNodes.length);
});

/* pronto */
manda('pronto');