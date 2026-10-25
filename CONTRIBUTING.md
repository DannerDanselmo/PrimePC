# Como contribuir com o PrimePC

Obrigado pelo interesse! Correções, novos sensores, melhorias na interface, testes em hardwares diferentes,
traduções e documentação são bem-vindos.

> **English:** contributions are welcome in English too. The UI and code comments are in Brazilian Portuguese,
> but issues and pull requests can be written in either language.

## Antes de começar

- **Bugs e ideias:** abra uma [issue](../../issues/new/choose) usando um dos modelos. Para mudanças grandes (uma aba
  nova, outra fonte de dados, troca de biblioteca), abra a issue antes de escrever o código, para combinarmos o caminho.
- **Falhas de segurança:** não abra issue pública. Siga a [política de segurança](SECURITY.md).

## Preparar o ambiente

- Windows 10 ou 11, 64 bits.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Opcional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) para gerar o instalador.
- Opcional: [PawnIO](https://pawnio.eu/) e o app aberto como administrador, para os sensores de baixo nível
  (temperatura da CPU, placa-mãe, SPD).

```powershell
dotnet build PrimePC.slnx                 # compila tudo
dotnet run --project src/PrimePC.App       # executa
dotnet test tests/PrimePC.Tests            # testes automatizados
./gerar-instalador.ps1                     # testes + executável + instalador (instalador\PrimePC-Setup-<versão>.exe)
```

FPS, tempo de quadro e ping (aba Jogos e análise de gargalo) usam eventos ETW do Windows e só funcionam com o app
aberto como administrador.

## Fluxo de contribuição

1. Faça um fork do repositório e crie um branch a partir do `main` (ex.: `correcao/temperatura-amd`, `melhoria/sensor-x`).
2. Faça a mudança com testes (veja abaixo) e rode `dotnet test` localmente.
3. Abra um pull request explicando o que mudou, por quê e como você testou (hardware, Windows, com ou sem administrador).
4. O CI compila e roda os testes em cada pull request. Ele precisa passar antes do merge.

Prefira pull requests pequenos e focados: um assunto por PR é revisado mais rápido.

## Princípios do projeto

Estas regras definem o PrimePC. Pull requests que as quebram não são aceitos:

- **Nenhum valor inventado.** Se o hardware, o driver ou a permissão não fornecem uma leitura, mostre "Não disponível"
  com o motivo. Nunca estime, preencha com zero ou copie de outro sensor sem deixar isso claro.
- **Nada muda sem o usuário pedir.** Nada de overclock, tensões, BIOS, configurações do Windows ou instalação de
  drivers automática. Toda ação que altera o sistema exige um clique explícito e, quando possível, oferece como desfazer.
- **Testes seguros.** Testes intensivos pedem confirmação, podem ser cancelados e têm proteções (corte térmico, reserva
  de memória). Testes destrutivos de disco ou de dados não existem.
- **Privacidade.** Nada sai do computador sem uma ação do usuário. Relatórios não incluem número de série nem nome de
  usuário. Não adicione telemetria, análise de uso nem chamadas a serviços de terceiros sem discutir antes numa issue.
- **Seguro para jogos com anti-cheat.** A sobreposição é uma janela comum e o FPS vem dos eventos do Windows. Não
  injete código, DLLs ou hooks em processos de jogos.
- **Downloads verificados.** Qualquer arquivo baixado (como drivers) vem só de domínios oficiais, por HTTPS, e tem a
  assinatura digital conferida antes de ser executado.

## Código

- C# / .NET 10, WPF, MVVM com CommunityToolkit.Mvvm. Siga o estilo do código ao redor.
- Nomes de tipos, métodos e variáveis em inglês. Textos da interface e comentários em português do Brasil.
- Cada módulo em `src/` é independente (veja a seção "Arquitetura" no [README](README.md)). Lógica nova vai no módulo
  certo, não na interface (`PrimePC.App`).
- Textos da interface: frases curtas e diretas, com o motivo quando algo não está disponível.

## Testes

- Lógica nova (cálculos, análise, parsing, regras de estado) precisa de testes xUnit em `tests/PrimePC.Tests`.
- Testes que dependem de hardware (GPU, sensores, privilégios) devem **pular sozinhos** quando o recurso não existe,
  como em `GpuTests.cs`. O CI roda numa máquina virtual sem placa de vídeo e sem administrador.
- Não crie testes que alterem o sistema (drivers, registro, tarefas agendadas) nem que apaguem arquivos fora de uma
  pasta temporária própria.

## Versões e releases

A versão fica em `Directory.Build.props`. Quem mantém o projeto atualiza a versão, gera o instalador com
`gerar-instalador.ps1` e publica a release com o SHA-256 do instalador.

## Licença

Ao enviar uma contribuição, você concorda que ela seja distribuída sob a [licença MIT](LICENSE) do projeto.
