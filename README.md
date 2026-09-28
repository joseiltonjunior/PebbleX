# pebbleX

**Objetivo:** usar os botões Easy-Switch do teclado para levar o mouse ao mesmo canal, com instalações PebbleX coordenadas pela rede local. O painel previsto mostra os computadores lado a lado, a localização confirmada dos periféricos e um controle para ativar/desativar a sincronização. O escopo está em [docs/PRODUCT.md](docs/PRODUCT.md).

Protótipo de um utilitário Windows voltado à sincronização do Easy-Switch entre periféricos Logitech. O Flow inclui descoberta local, vínculo com código de verificação e sincronização entre dois PCs Windows. A descoberta/pareamento automático, as trocas nos dois sentidos e a execução em segundo plano pela área de notificação foram confirmados pelo usuário em hardware. O **Modo desenvolvedor** mantém enumeração HID, captura e controles manuais.

## Abrir a bancada visual

No Windows, dê dois cliques em **Abrir PebbleX.cmd** na raiz do projeto. O inicializador compila usando o SDK definido em `global.json` e abre a janela. A tela principal mostra o resumo dos periféricos; **Modo desenvolvedor** abre a bancada com captura e comandos manuais. Também é possível executar `dotnet run --project src/PebbleX.Desktop` ou iniciar o projeto `PebbleX.Desktop` pelo Visual Studio.

### Configurar o Flow entre dois PCs Windows

1. Conecte os dois PCs à mesma rede local, abra o PebbleX e clique em **Vincular computador** nos dois. Deixe o Flow pausado durante o pareamento.
2. Selecione o outro PC encontrado. Compare o código de 8 dígitos mostrado nas duas telas e confirme em ambas. A chave é derivada durante o pareamento, e cada instalação a protege pelo DPAPI do Windows.
3. Ative o Flow nos dois PCs. Ao trocar o teclado, o computador de destino confirma o canal, o par que ainda tem acesso ao mouse envia a troca e o destino consulta o mouse após reconectar.

O anúncio de descoberta usa UDP `47630`; o pareamento e o Flow compartilham TCP `47631` em modos exclusivos. Permita o PebbleX na rede privada do Windows. Se a descoberta não funcionar, verifique o isolamento entre clientes Wi-Fi ou use **Configuração manual (avançado)**. Os números dos canais precisam apontar para o mesmo computador nos dois periféricos.

Com o Flow ativo, minimizar mantém a janela na barra de tarefas. Fechar pelo **X** recolhe o PebbleX para a área de notificação e mantém o Flow ativo. Clique duas vezes no ícone para reabrir; o menu do botão direito permite abrir ou encerrar o programa. Encerrar pelo menu interrompe o Flow.

1. Selecione o teclado ou mouse e clique em **Iniciar captura**. A lista mostra interfaces Logitech `FF43/0202` com relatórios de entrada; o identificador do produto ajuda a conferir a seleção.
2. Acione o Easy-Switch e acompanhe os horários de saída/retorno e eventuais relatórios no registro. A sessão tenta reabrir a mesma interface após a reconexão.
3. Escreva uma observação, como `teclado 1 → 2`, e clique em **Marcar evento** para registrar a ação manualmente.
4. Use **Parar captura**, **Salvar registro…** ou **Encerrar**. O fechamento cancela a sessão e libera a leitura. O registro mantém as últimas 1.000 linhas; excesso de tráfego é sinalizado.

A detecção de presença usa amostragem a cada segundo, então uma saída e volta muito rápidas podem passar entre amostras. O canal de destino não é deduzido automaticamente. Se o Windows atribuir outro caminho à interface, pare, atualize o inventário e selecione novamente o dispositivo.

Com a captura parada, a lista verifica conexões a cada 5 segundos. Dispositivos vistos nesta sessão permanecem na lista como **aguardando reconexão** quando sua interface some. Isso não indica que estão conectados; a consulta de canal só fica disponível quando a interface está presente. Ao reiniciar o aplicativo, o inventário é reconstruído a partir das interfaces disponíveis.

O Windows pode manter o serviço Bluetooth HID de um Logitech no inventário sem expor a interface HID++ que o PebbleX lê. Nesse caso, a lista mostra **Bluetooth reconhecido; HID indisponível** e mantém os botões de captura/consulta desabilitados para essa entrada. Isso informa o motivo da ausência sem apresentar um canal não confirmado. `dotnet run --project src/PebbleX.Cli -- bluetooth` lista esses serviços para diagnóstico.

### Consultar o canal

Com a captura parada, selecione o dispositivo conectado e clique em **Consultar canal**. O resultado mostra o canal físico (1 a 3) e a hora da consulta. É uma leitura pontual: após uma troca, consulte novamente. Para validar a escrita manual, selecione o mouse B034, escolha o destino e clique em **Mover mouse**. O comando de troca não recebe resposta; depois da reconexão, selecione o mouse e consulte o canal para confirmar. A interface distingue solicitação enviada de canal confirmado.

Pelo CLI: `dotnet run --project src/PebbleX.Cli -- channel --product B377` para o teclado observado, ou `B034` para o mouse. O comando `controls --product B377` consulta a tabela de controles e suas capacidades, sem remapear teclas ou ativar eventos. As consultas exigem uma única interface Logitech `FF43/0202` com entrada e saída de 20 bytes.

Nos testes de 25/09/2026, o teclado informou canal **1** e o mouse canal **3** neste PC. O usuário decidiu reorganizar os pareamentos para números iguais: 1 no PC principal, 2 no Windows secundário e 3 no Linux secundário ou telefone. A reorganização ainda deve ser conferida com nova consulta.

## Projetos

- `PebbleX.Core`: domínio e protocolo, independente de plataforma.
- `PebbleX.Windows`: descoberta e monitoramento HID no Windows, dependente apenas de `PebbleX.Core`.
- `PebbleX.Cli`: comandos de diagnóstico `devices`, `monitor`, `channel` e `controls`, dependente de `PebbleX.Core` e `PebbleX.Windows`.
- `PebbleX.Desktop`: bancada WPF de diagnóstico, dependente de `PebbleX.Windows`.

Os detalhes de arquitetura, segurança, protocolo, testes, referências e observações de hardware estão em `docs/`.
