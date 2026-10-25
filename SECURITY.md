# Política de segurança

## Versões com suporte

Correções de segurança saem para a versão mais recente publicada em [Releases](../../releases).

## Como relatar uma vulnerabilidade

**Não abra uma issue pública.** Use o relato privado do GitHub:

1. Abra a aba **Security** do repositório.
2. Clique em **Report a vulnerability**.
3. Descreva o problema, a versão do PrimePC, a versão do Windows e os passos para reproduzir.

O relato fica visível só para quem mantém o projeto. Você recebe uma resposta assim que possível. Quando a correção
for publicada, o relato pode ser divulgado, com crédito a você se quiser.

## Pontos sensíveis do aplicativo

Estas partes merecem atenção especial em relatos e em revisões de código:

- **Painel no celular** (`PhoneDashboardServer.cs`): servidor HTTP na rede local, somente leitura, protegido por
  código de acesso.
- **Download e instalação de drivers** (`PrimePC.Drivers`): só HTTPS em domínios oficiais e conferência da assinatura
  digital (Authenticode) antes de executar.
- **Execução como administrador**: reinício elevado, tarefa agendada "Iniciar com o Windows" e sessão ETW de eventos.
- **Relatórios e exportações**: não devem conter número de série, nome de usuário ou nome do computador sem opção
  explícita.
