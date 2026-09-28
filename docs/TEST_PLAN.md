# Plano de testes

Gates do projeto, nesta ordem:

1. Enumeration — implementado e executado em hardware real com `dotnet run --project src/PebbleX.Cli -- devices`.
2. Passive monitoring — implementado e executado em collections `FF43/0202`; os resultados estão em `OBSERVATIONS.md`.
3. Host event identification — canal consultado depois da chegada do teclado; eventos analíticos não são usados porque a notificação antes da desconexão ainda não foi validada.
4. Manual mouse write — implementada e validada em hardware pelo usuário.
5. Rede local — Flow bidirecional e descoberta/pareamento ECDH com código foram exercitados nos dois PCs reais. A rede precisa permitir UDP 47630 para descoberta e TCP 47631 em cada direção; bloqueio de tráfego entre clientes ou perfil/firewall podem impedir a conexão.
6. End-to-end sync — teclado 1→2 e retorno 2→1 sincronizaram mouse e canal confirmado nos dois PCs. O usuário observou atraso de 1–3 segundos; medir por etapa e cobrir trocas rápidas, perda de rede e suspensão.
7. Stability — perda de rede, eventos repetidos/atrasados, troca rápida, intervenção manual e encerramento durante transferência ainda pendentes nos dois PCs.
8. UI — tela Flow simplificada, modo desenvolvedor, assistente de descoberta/vínculo e permanência na área de notificação com Flow ativo implementados. Descoberta/vínculo e uso em segundo plano foram confirmados pelo usuário em hardware; automatizar/registrar o teste de ciclo de vida continua pendente.

## Execução automatizada registrada em 28/09/2026

- `dotnet test pebbleX.sln --configuration Release --no-restore --nologo`: 54 testes aprovados, 0 falhas, 0 ignorados (1 em Core e 53 em Windows).
- `dotnet build src/PebbleX.Desktop/PebbleX.Desktop.csproj --configuration Release --no-restore --nologo`: compilação WPF aprovada sem warnings ou erros após a implementação da bandeja.
- A publicação self-contained single-file para `win-x64` foi concluída. Testes automatizados não exercitam UI WPF nem substituem o teste de hardware.

## Bancada de diagnóstico

- Testes automatizados da sessão: reconectar somente a interface escolhida; ignorar outro dispositivo com identificadores iguais; liberar a leitura ao cancelar ou falhar a enumeração; rejeitar collections fora do escopo de diagnóstico.
- Teste manual: selecionar um dispositivo, iniciar, trocar seu canal, voltar e observar a captura retomar; marcar uma ação; parar; salvar; encerrar.
- Verificar que a janela permanece responsiva durante leituras sem relatórios e durante a ausência do dispositivo.

## Consulta ativa de diagnóstico

- Testes automatizados: índice de feature resolvido dinamicamente; correlação de respostas e erros; descarte de eventos/outros software IDs; feature ausente; canal inválido; conversão 0–2 para 1–3; cancelamento; tempo limite; interpretação dos dois bytes de flags; rejeição de dispositivo não identificado.
- Histórico de hardware (25/09): teclado B377 confirmou canal 1/3 e mouse B034 canal 3/3 antes da reorganização dos pareamentos. Consulte os canais atuais antes de novos testes físicos.
- O gatilho atual é a chegada/reconexão do teclado e a consulta de ChangeHost. Os eventos D1/D2/D3 seguem como investigação opcional para reduzir latência; ainda não foram validados como sinal confiável antes da desconexão.
- O comando manual e o transporte de rede já foram validados no fluxo ponta a ponta; a origem só comanda o mouse enquanto tem acesso à interface HID local.
- Para a volta a partir de Linux ou telefone, implementar/validar o agente e o suporte HID em cada sistema. O retorno a partir do telefone permanece uma questão de viabilidade.

## Aceitação da sincronização em rede

- Descobrir outra instalação não a vincula automaticamente. Versão incompatível ou ausência de suporte HID devem aparecer na interface.
- Teclado 1→2 e 2→1: o mouse acompanha e cada UI só mostra sincronizado após a confirmação do destino.
- Mouse trocado fisicamente: permanecer onde o usuário o colocou até nova transição do teclado ou ação explícita de alinhar.
- Desativar: não enviar novos comandos nem reaplicar pedidos pendentes ao reativar.
- Duplicação, reconexão de sessão, perda de rede, expiração e propostas conflitantes: não executar comandos com observações antigas; não apresentar estado sincronizado sem confirmação recente.
- Ao suspender/reiniciar um agente, sua chegada à rede não deve ser confundida com um acionamento físico do teclado.
- A conexão por rede não substitui o pareamento Bluetooth dos canais. Conferir o mapa antes do teste.

## Histórico: interface HID do mouse B034

- Durante os testes, o Windows reconhecia o serviço Bluetooth, mas não expunha a interface HID++ B034. Depois da reorganização/reconexão, o usuário confirmou que o PebbleX voltou a listar e capturar o mouse.
- Essa ocorrência está resolvida no cenário atual. Se voltar a acontecer, cursor funcionando não basta para confirmar que a interface de diagnóstico está ativa; conferir o inventário e o estado Bluetooth antes de enviar comandos.
