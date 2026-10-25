# PrimePC — Monitoramento e diagnóstico de PC

[![CI](https://github.com/DannerDanselmo/PrimePC/actions/workflows/ci.yml/badge.svg)](https://github.com/DannerDanselmo/PrimePC/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/DannerDanselmo/PrimePC?color=ff2340&label=vers%C3%A3o)](https://github.com/DannerDanselmo/PrimePC/releases/latest)
[![Licença MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-ff7a18)](LICENSE)
[![Contribuições bem-vindas](https://img.shields.io/badge/contribui%C3%A7%C3%B5es-bem--vindas-2ee57d)](CONTRIBUTING.md)

> **English:** PrimePC is an open-source Windows app for hardware monitoring, diagnostics, stress tests, driver
> updates and an in-game overlay (FPS, frame time, 1% low, temperatures, ping). Built with C# / .NET 10 and WPF.
> The UI is in Brazilian Portuguese. Contributions, including an English translation, are welcome: see
> [CONTRIBUTING.md](CONTRIBUTING.md).

Aplicativo desktop para Windows que identifica o hardware, exibe todos os sensores acessíveis em tempo real,
analisa possíveis gargalos (CPU, GPU, RAM, VRAM, armazenamento, temperatura), testa a memória RAM, executa
testes de estabilidade/benchmarks e gera relatórios em PDF, CSV e JSON.

Princípio central: **nenhum valor é inventado**. Quando o hardware, o driver ou a permissão não fornecem uma
leitura, ela aparece como **"Não disponível"**, com o motivo na dica (passe o mouse sobre o valor).

> **Antes chamado PulsoPC.** A versão 2.0 trocou o nome e a identidade visual (preto, vermelho e laranja, títulos
> condensados em maiúsculas, botões e abas inclinados). Ao atualizar, o instalador substitui o PulsoPC (mesma
> instalação, nova pasta `PrimePC`), remove a pasta, os atalhos e a tarefa de inicialização antigos, e o programa traz
> sozinho as configurações e o histórico de `%LOCALAPPDATA%\PulsoPC` para `%LOCALAPPDATA%\PrimePC` na primeira execução.

## Baixar

Baixe o `PrimePC-Setup-<versão>.exe` na página de **[Releases](https://github.com/DannerDanselmo/PrimePC/releases/latest)**
e execute. Não precisa instalar o .NET.

O instalador não tem assinatura digital de código. Por isso, o Windows SmartScreen pode mostrar "O Windows protegeu o
computador": clique em **Mais informações** > **Executar assim mesmo**. Para conferir se o arquivo é o original,
compare o SHA-256 publicado na release com o resultado de:

```powershell
Get-FileHash .\PrimePC-Setup-2.0.0.exe -Algorithm SHA256
```

---

## Funcionalidades (abas)

| Aba | O que faz |
|---|---|
| **Visão geral** | Painel com CPU, GPU, RAM, placa-mãe (temperaturas de VRM/chipset/sistema, ventoinhas, bomba e tensões 12 V/5 V/3,3 V/Vcore), discos e sistema; minigráficos ao vivo; sensores favoritos (reordenáveis); detalhes completos do hardware (CPU, placa-mãe/BIOS, módulos de RAM, GPUs). |
| **Sensores** | Todos os sensores detectados, agrupados por componente, com valor, mínimo, média, máximo, unidade, tipo, estado (normal/atenção/crítico/indisponível) e origem. Busca, filtros, favoritos, ocultar, gráfico histórico, criação de alertas e exportação do histórico em CSV. |
| **Gargalo** | Grava telemetria enquanto você joga/usa um programa e conclui, com nível de confiança ("provável", "possível", "não foi possível concluir"), o que limita o desempenho. Mede o FPS de cada quadro do jogo em primeiro plano (como administrador) e mostra a **fluidez**: FPS médio, 1% low, 0,1% low, travadas, pior quadro e o gráfico do tempo de cada quadro. Mostra evidências, condições, sugestões não invasivas e limitações. Sessões ficam salvas e podem ser comparadas (inclusive FPS e 1% low). |
| **Teste de RAM** | Modos rápido (6 padrões), completo (25 padrões, várias passagens) e por duração. Progresso, cancelamento seguro, lista de erros (padrão, passagem, deslocamento, valor esperado/lido) e atalho para o teste inicializável do Windows (`mdsched.exe`). Inclui o **teste de VRAM** (módulo separado): 8 padrões verificados na própria GPU, em blocos de 256 MB, com limite seguro calculado pela VRAM livre. |
| **Estabilidade** | Teste de CPU com verificação de cada resultado contra uma referência (detecta erros de cálculo) e corte térmico automático; **teste de GPU** (carga de computação DirectX 12 calibrada, comparação de cada execução na própria GPU, corte térmico, temperatura/clock/potência ao vivo); teste combinado CPU + RAM + GPU; benchmarks de CPU (1 thread e todas) e de memória (largura de banda e latência) com comparação com execuções anteriores. |
| **Armazenamento** | Saúde dos discos (Windows), contadores de confiabilidade (horas ligado, desgaste, erros), sensores SMART/NVMe e teste de velocidade **não destrutivo** (arquivo temporário próprio, apagado ao final). |
| **Drivers** | Todos os drivers instalados, separados em abas (Atenção, Vídeo, Áudio, Rede, Bluetooth, Armazenamento, USB, Periféricos, Sistema e chipset…), com fabricante, versão (NVIDIA no formato do fabricante, ex.: 617.42), data e assinatura digital. Mostra dispositivos com problema (sem driver, com erro, desativados) com o motivo. **Procura atualizações** no Windows Update (qualquer fabricante) e, para placas **NVIDIA**, no serviço oficial da NVIDIA — e **baixa e instala** o que você escolher: os drivers do Windows Update são instalados pelo próprio Windows; o da NVIDIA é baixado do servidor oficial (o download pausado continua de onde parou), tem a assinatura digital da NVIDIA conferida e abre no instalador oficial. Opção de criar um ponto de restauração antes de cada instalação. Atalhos para o Gerenciador de Dispositivos, o Windows Update e o site do fabricante. |
| **Jogos** | Quatro seções. **Sobreposição**: FPS, tempo de quadro (ms), 1% low, gráfico de tempo de quadro, uso, temperatura e potência da CPU e da GPU, clock da GPU, VRAM, RAM, hora e ping num canto da tela, por cima do jogo; liga e desliga pelo botão ou pelo atalho global (padrão Ctrl + Shift + F10); **perfis por jogo** (visual próprio e "ligar sozinha" quando o jogo abre). **Benchmark**: grava cada quadro por 30 s a 5 min (atalho Ctrl + Shift + F9 dentro do jogo) e mostra FPS médio, 1% low, 0,1% low, travadas, pior quadro e o gráfico, com histórico e comparação com o benchmark anterior do mesmo jogo. **Sessões de jogo**: registra sozinho cada sessão de 2 min ou mais (duração, FPS, temperaturas máxima e média de CPU e GPU) e avisa se o PC está esquentando mais no mesmo jogo com o tempo. **Celular**: painel só leitura no navegador do celular (mesma rede Wi-Fi, com QR code e código de acesso). FPS medido pelos eventos do próprio Windows, sem injetar nada no jogo. |
| **Relatórios** | Relatório com especificações, sensores (mín./média/máx.), alertas, testes, benchmarks e análise de gargalo. Exporta PDF, CSV (formato do Excel brasileiro: `;` e vírgula decimal) e JSON. O nome do computador só é incluído se você marcar. |
| **Configurações** | Tema (seguir o Windows / claro / escuro), intervalo de leitura, duração do histórico, °C/°F, alertas, pasta de dados, exportar/apagar histórico, log técnico e informações de compatibilidade. |

---

## Arquitetura e tecnologia

- **C# / .NET 10** e **WPF** com o tema Fluent nativo do Windows 11 (claro/escuro).
- Sensores via **[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** (código aberto, mantido) e APIs documentadas do Windows (WMI/SMBIOS, Storage Management API, `GlobalMemoryStatusEx`, `GetPerformanceInfo`).
- Testes de GPU/VRAM via **[ComputeSharp](https://github.com/Sergio0694/ComputeSharp)** (MIT): shaders DirectX 12 escritos em C# e compilados durante o build.
- PDF via **PDFsharp/MigraDoc** (MIT). MVVM com **CommunityToolkit.Mvvm**.

Módulos independentes (um projeto cada):

```
src/
  PrimePC.Sensors      Camada de abstração de fontes de sensores, avaliação de estado, alertas,
                       histórico (SensorHub) e identificação de hardware (inventário).
  PrimePC.Diagnostics  Motor de diagnóstico: análise de gargalo, sessões e comparação.
  PrimePC.Testing      Teste de RAM, teste de estabilidade da CPU, teste de armazenamento,
                       benchmarks, histórico de resultados e detecção de teste interrompido.
  PrimePC.GpuTesting   Módulo independente da placa de vídeo: teste de carga (estabilidade) e teste de VRAM.
  PrimePC.Drivers      Inventário de drivers, dispositivos com problema, atualizações (Windows Update e NVIDIA),
                       download com retomada, verificação de assinatura digital, ponto de restauração e instalação.
  PrimePC.Gaming       FPS e tempo de quadro (eventos ETW do Windows), captura de cada quadro, servidor do jogo e
                       ping, para a sobreposição e a análise de gargalo.
  PrimePC.Reports      Montagem do relatório e exportação PDF / CSV / JSON.
  PrimePC.App          Interface gráfica (WPF): telas, gráficos e configurações.
tests/
  PrimePC.Tests        Testes automatizados (xUnit).
```

---

## Requisitos

- Windows 10 ou 11, 64 bits.
- Para compilar: [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A versão publicada (`publicar.ps1`) é autocontida: não precisa instalar o .NET.

## Compilar, executar e testar

```powershell
dotnet build PrimePC.slnx                 # compila tudo
dotnet run --project src/PrimePC.App       # executa
dotnet test tests/PrimePC.Tests            # testes automatizados
./publicar.ps1                             # gera publicado\PrimePC.exe (arquivo único, autocontido)
./gerar-instalador.ps1                     # testes + executável + instalador (exige o Inno Setup 6)
```

Opções de linha de comando: `--aba=N` abre direto numa aba (0 = Visão geral … 6 = Drivers, 7 = Jogos … 9 = Configurações);
`--largura=N` e `--altura=N` definem o tamanho inicial da janela.

## Instalação e remoção

- **Instalar:** use o instalador da página de [Releases](https://github.com/DannerDanselmo/PrimePC/releases/latest). Versão
  portátil: copie a pasta `publicado` para onde quiser (ex.: `C:\Programas\PrimePC`) e crie um atalho para `PrimePC.exe`.
- **Remover:** Configurações do Windows > Aplicativos > PrimePC > Desinstalar (na versão portátil, apague a pasta do
  programa). Os dados ficam em `%LOCALAPPDATA%\PrimePC`.
- O PrimePC não instala serviços. Drivers só são instalados quando você pede, na aba Drivers.
- **Ícone perto do relógio e inicialização**: em Configurações, "Ao fechar a janela, manter o PrimePC rodando perto do relógio" e "Iniciar com o Windows". Esta cria uma tarefa agendada "PrimePC" (com privilégios de administrador, no seu logon), removida ao desmarcar ou ao desinstalar. Para sair de verdade, use "Sair do PrimePC" no menu do ícone.
- Instaladores de driver baixados ficam em `%LOCALAPPDATA%\PrimePC\downloads` e são apagados depois que a instalação é confirmada.

---

## Sensores, permissões e driver

O app inicia **sem administrador** (privilégio mínimo). A elevação é opcional, pelo botão
"Reiniciar como administrador".

| Dados | Sem administrador | Administrador | Administrador + PawnIO |
|---|:---:|:---:|:---:|
| Uso da CPU (total e por thread) | ✔ | ✔ | ✔ |
| Temperatura, clocks, potência e tensões da CPU | — | — | ✔ |
| GPU NVIDIA / AMD / Intel (uso, temperatura, clocks, potência, VRAM, ventoinhas) | ✔ | ✔ | ✔ |
| Placa-mãe: ventoinhas, tensões (12 V, 5 V, 3,3 V), VRM/chipset | — | — | ✔ |
| Memória RAM: uso, disponível, comprometida | ✔ | ✔ | ✔ |
| Temperatura dos módulos de RAM (SPD) | — | — | ✔ |
| Discos: saúde informada pelo Windows, espaço livre | ✔ | ✔ | ✔ |
| Discos: temperatura, SMART/NVMe, contadores de confiabilidade | — | ✔ | ✔ |
| Rede, bateria, sistema (processos, tempo ligado) | ✔ | ✔ | ✔ |

### Sobre o PawnIO

O **PawnIO** é um driver de kernel **assinado e de código aberto**, usado pelo LibreHardwareMonitor para ler
registradores da CPU e chips da placa-mãe. Ele substituiu o antigo WinRing0, que o Windows Defender passou a
bloquear por ser vulnerável.

**O PrimePC nunca instala drivers sozinho.** Se quiser os sensores de baixo nível:

1. Instale o PawnIO pelo site oficial (<https://pawnio.eu/>) ou pelo winget: `winget install namazso.PawnIO`
2. Abra o PrimePC como administrador.

Para remover: Configurações do Windows > Aplicativos > PawnIO > Desinstalar.

Sem o driver, o LibreHardwareMonitor devolve zeros/valores padrão para esses sensores; o PrimePC detecta isso e
mostra "Não disponível" em vez de exibir números falsos.

---

## Limitações conhecidas

- **Nenhum programa acessa literalmente todo sensor de todo PC**: depende do fabricante, firmware, driver e permissões.
- **Gargalo**: o resultado vale para as condições da gravação (jogo, cena, resolução, configurações, limite de FPS).
  Não existe "percentual universal de gargalo", e o PrimePC não mostra um.
- **FPS/frametime (sobreposição e gargalo)**: medidos pelos eventos ETW que o Windows publica a cada quadro (DXGI, Direct3D 9
  e, para Vulkan/OpenGL fora do DXGI, o kernel gráfico). Exigem administrador. Mostram o ritmo em que o jogo entrega
  quadros (como o PresentMon "MsBetweenPresents"), não o momento exato em que cada quadro aparece no monitor.
  Sem administrador, a análise de gargalo usa o FPS informado pelo driver, quando existir.
- **Sobreposição**: é uma janela comum por cima do jogo (nada é injetado no jogo). Aparece em modo janela e em tela
  cheia sem bordas; em tela cheia exclusiva pode não aparecer.
- **Ping**: é o ping (ICMP) até o servidor para o qual o jogo envia pacotes. Muitos servidores de jogos não respondem a
  ping; nesse caso a sobreposição mostra REDE, a latência até um endereço de referência (padrão 1.1.1.1), que indica a
  qualidade da conexão mas não é o ping exato do jogo.
- **Teste de RAM** roda dentro do Windows: a memória ocupada pelo sistema e pelos programas não pode ser testada,
  e os endereços mostrados são virtuais. Para alcançar mais memória, use o teste inicializável (`mdsched.exe` ou MemTest86).
- **Teste de GPU** usa carga de computação (DirectX 12); jogos também usam rasterização, texturas e ray tracing, que podem
  revelar instabilidades diferentes. Para observar a GPU num jogo, grave uma sessão na aba Gargalo.
- **Teste de VRAM** não alcança a memória de vídeo em uso pelo Windows, pela tela e por outros programas; o driver decide
  onde cada bloco fica (por isso há uma reserva de 1 GB), e os endereços mostrados são relativos à área de teste.
- **Drivers desatualizados**: as fontes usadas são o Windows Update, que só oferece drivers testados para o seu
  hardware, e, para placas de vídeo NVIDIA, o serviço oficial da NVIDIA (driver Game Ready mais recente). AMD e Intel
  não oferecem uma consulta automática equivalente: para elas o PrimePC mostra a versão instalada e um atalho para o
  site do fabricante, sem afirmar que existe versão nova. Drivers de chipset, áudio e rede publicados só no site do
  fabricante da placa-mãe também não são detectados. A data do driver sozinha não indica desatualização (muitos
  drivers do próprio Windows mostram datas antigas e estão corretos).
- **Instalação de drivers**: exige abrir o PrimePC como administrador (o instalador da NVIDIA pede a permissão
  sozinho). O Windows cria no máximo um ponto de restauração a cada 24 horas; se já houver um recente, ele é usado.
  Durante a instalação do driver NVIDIA a leitura de sensores fica pausada, e o PrimePC reabre sozinho ao final para
  carregar o driver novo e conferir a versão.
- **Chipset** e **perfis XMP/EXPO** não são expostos pelo Windows de forma padronizada; o app informa a velocidade
  configurada e a velocidade informada pelo SMBIOS.
- Benchmarks usam pontuações próprias, comparáveis apenas entre execuções no mesmo computador.
- Limites de temperatura padrão são conservadores e dependem do modelo (ex.: Ryzen X3D: atenção 80 °C, crítico 89 °C);
  limites personalizados podem ser definidos como alertas.

## Segurança e privacidade

- Histórico, sessões e resultados ficam em `%LOCALAPPDATA%\PrimePC`. O PrimePC só usa a internet quando você clica em
  "Procurar atualizações" ou instala um driver na aba Drivers (o Windows Update recebe os identificadores dos
  dispositivos, como nas Configurações do Windows, e a NVIDIA recebe só o modelo da placa, a versão do Windows e o
  idioma); com a sobreposição ligada e o ping ativado, para enviar um ping por segundo ao servidor do jogo ou ao
  endereço de referência; e, com o painel no celular ligado, para responder na rede local (somente a quem tem o código
  de acesso, somente leitura, sem nome do computador nem do usuário).
- Nenhuma alteração automática de overclock, tensões, BIOS ou configurações do sistema.
- Testes intensivos pedem confirmação, podem ser interrompidos a qualquer momento e têm proteções:
  corte térmico (CPU), reserva de memória para o Windows e interrupção automática se a memória livre acabar (RAM),
  arquivo temporário próprio e apagado ao final (armazenamento). Testes destrutivos não existem.
- Se o computador travar ou reiniciar durante um teste, o PrimePC avisa na próxima abertura.
- Relatórios não incluem números de série nem nome de usuário; o nome do computador só com opção explícita.
- Encontrou uma falha de segurança? Relate em particular, como explica a [política de segurança](SECURITY.md).

---

## Contribuir

O PrimePC é aberto a contribuições: correções, sensores novos, telas, testes em outros hardwares e traduções. Leia o
[guia de contribuição](CONTRIBUTING.md) (ambiente, fluxo de pull request e princípios do projeto) e o
[código de conduta](CODE_OF_CONDUCT.md).

Boas primeiras contribuições:

- Tradução da interface para inglês.
- Consulta de drivers novos para placas AMD e Intel (hoje só NVIDIA e Windows Update).
- Testes e correções de sensores em placas-mãe, notebooks e GPUs diferentes.
- Melhorias de acessibilidade (leitores de tela, navegação por teclado).

## Licença

[MIT](LICENSE) © DannerDanselmo. As bibliotecas usadas mantêm as próprias licenças: LibreHardwareMonitorLib (MPL-2.0),
ComputeSharp, PDFsharp/MigraDoc, CommunityToolkit.Mvvm, Microsoft.Diagnostics.Tracing.TraceEvent e QRCoder (MIT).
