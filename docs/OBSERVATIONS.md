# Observações de hardware

## 2026-09-28 — descoberta, Flow bidirecional e execução em segundo plano

- O usuário confirmou a descoberta do outro PC, comparação do código de segurança nas duas telas e vínculo automático. A UI havia exibido o nome de tipo interno na lista; isso foi corrigido para mostrar o rótulo do computador e o botão **Voltar ao Flow** foi adicionado à tela de vínculo.
- O usuário confirmou o ciclo de sincronização teclado→mouse nos dois sentidos entre dois PCs Windows. Os dois PebbleX precisam estar ativos e vinculados. TCP `47631` deve ser alcançável em ambas as direções; uma falha anterior de `DestinationHostUnreachable` foi resolvida após ajustes de conectividade da rede. Estar no mesmo SSID, por si só, não garantiu alcance entre os clientes.
- O usuário observou atraso de aproximadamente 1–3 segundos entre a troca do teclado e a resposta do mouse. Ainda não há medição segmentada para atribuir o tempo ao Bluetooth, à amostragem HID ou à rede.
- O mouse B034, que em um momento aparecia como Bluetooth reconhecido sem interface HID, voltou a ser listado e capturado após a reorganização/reconexão dos canais. O usuário confirmou captura do mouse e do teclado no PebbleX.
- Com o Flow ativo, fechar a janela pelo X agora oculta a janela e mantém os listeners; o ícone na área de notificação permite reabrir ou encerrar. O usuário confirmou que o Flow funcionou em segundo plano. Minimizar mantém o comportamento padrão da janela.
- Em 28/09/2026, `dotnet test pebbleX.sln --configuration Release --no-restore --nologo` passou com 54 testes: 1 Core e 53 Windows. O build Release da aplicação desktop passou sem warnings ou erros. Esses testes cobrem lógica local; não exercitam UI nem hardware.

Pendências: medir latência por etapa; testar trocas rápidas, perda de rede, suspensão/retorno e encerramento durante uma transferência; atualizar os registros de hardware com uma nova consulta dos canais atuais. Linux e telefone ainda não foram validados como participantes.

## 2026-09-25 — consulta ativa de canal e controles

O usuário informou o destino desejado dos canais Bluetooth: 1 no PC principal, 2 no Windows do PC secundário, 3 no Linux desse PC ou, ocasionalmente, no telefone.

- Às 10:33:04, B377 respondeu a ROOT.getFeature(1814) com índice `0A`, versão 1. getHostInfo retornou `03 00`: três hosts, canal físico 1.
- Na consulta da tabela `1B04`, B377 anunciou índice `09`, versão 5, 15 controles. Os primeiros foram D1/D2/D3, com flags `040A`: evento analítico suportado, desvio comum não suportado. Registro local: `artifacts/controls-keyboard-20260925.txt`.
- B034 esteve ausente em algumas enumerações durante os testes. Voltou a aparecer via Bluetooth e respondeu às 10:38:38: ROOT.getFeature(1814) → `0A`, versão 1; getHostInfo → `03 02`, canal físico 3. Registro local: `artifacts/channel-mouse-20260925.txt`. Não foi necessário acessar o receptor USB.
- Os canais confirmados neste PC eram diferentes entre teclado e mouse. O usuário decidiu reorganizar os pareamentos para números iguais. Isso é uma ação pendente do usuário, não um pareamento alterado pela aplicação.
- A bancada passou a oferecer **Consultar canal**, com resultado pontual e horário, tratamento de erro/cancelamento e consulta desabilitada durante captura. As consultas não alteram o canal nem a configuração de eventos das teclas.

Ainda falta observar uma notificação que revele o canal de destino antes da desconexão e validar a troca comandada do mouse. Capacidade anunciada pelo teclado não é prova de entrega do evento.

### Mouse ausente na lista

O usuário relatou que o cursor continuava funcionando, mas o mouse sumiu da lista. Naquele momento, a enumeração HID não continha B034. A consulta PnP específica retornou `Unknown / CM_PROB_PHANTOM` na collection de mouse B034. A presença do registro Bluetooth de pareamento não confirmou uma interface HID ativa. O receptor C548 permanecia presente, mas não foi confirmado como origem do cursor.

A bancada foi corrigida para preservar dispositivos vistos durante a sessão como aguardando reconexão e atualizar automaticamente o inventário a cada 5 segundos quando parada. A lista não assume que um dispositivo ausente esteja conectado. A consulta PnP também forneceu os nomes Bluetooth: B034 é MX Master 3S e B377 é Pebble K380s.

Após o usuário reorganizar mouse e teclado para usar canais 1 neste PC e 2 no secundário, o mouse deixou de aparecer na lista HID++. O usuário informou que o cursor continua funcionando no canal 1 via Bluetooth. No Windows, o dispositivo `BTHLE\\DEV_D67D8AEB2790` (MX Master 3S) e seu serviço Bluetooth HID `1812` estão presentes; as collections filhas `B034` de mouse e de diagnóstico permanecem `Unknown`/inativas. O inventário HID++ expõe apenas B377. A origem exata do movimento do cursor não foi medida por este diagnóstico; o receptor C548 ainda consta no inventário USB do Windows, embora o usuário informe que não está em uso. Uma varredura `pnputil /scan-devices` foi tentada, mas o Windows a negou por falta de privilégio administrativo. Nenhum pareamento ou driver foi removido.

Foi adicionada uma segunda fonte de diagnóstico, somente leitura, que enumera serviços Bluetooth HID Logitech presentes no Configuration Manager. Ela confirma B034, B020 e B377 no inventário de serviços, mesmo sem interfaces HID++ ativas. A UI passa a mostrar B034 como **Bluetooth reconhecido; HID indisponível** com captura e consulta desabilitadas até o Windows oferecer a interface. Isso não comprova canal atual, conexão utilizável para HID++ ou que o cursor venha desse dispositivo.

## 2026-09-25 — início da identificação do evento de troca de host

Ambiente: Windows, collections Logitech enumeradas pelo CLI. Foram encontradas as collections vendor-specific `046D:B034 FF43/0202` e `046D:B377 FF43/0202`. O produto `B034` também expõe uma collection de mouse (`0001/0002`); o `B377`, uma de teclado (`0001/0006`). A identidade comercial dos periféricos ainda não foi confirmada.

- Linha de base: cada collection `FF43/0202` foi monitorada por 5 segundos, sem acionar o Easy-Switch. Ambas abriram normalmente e produziram 0 relatórios.
- Primeira captura: monitoramento de `B034` por 30 segundos enquanto o usuário acionou o Easy-Switch do **teclado**, não do mouse. Foram recebidos 22 relatórios de 20 bytes, em dois grupos de 11 por volta de `10:01:03` e `10:01:09`. Ambos os grupos tinham a mesma sequência. Esses dados não podem ser atribuídos a uma troca de canal do mouse.
- Segunda captura: monitoramento de `B377` por 30 segundos enquanto o usuário acionou o Easy-Switch do teclado. Nenhum relatório apareceu antes de `FileStream.ReadAsync` lançar `IOException` com a mensagem “O dispositivo não está conectado.” O CLI encerrou com uma exceção sem tratamento. Após a volta ao canal original, o inventário voltou a mostrar a collection `B377 FF43/0202`.
- Terceira captura: monitoramento de `B377 FF43/0202` e amostragem paralela do inventário HID durante outra troca do teclado. `B377` estava presente às `10:05:15.282`, ausente às `10:05:31.036` e presente novamente às `10:05:34.184`. O monitor recebeu 0 relatórios antes da desconexão. Após a correção do CLI, a mesma `IOException` foi apresentada como mensagem controlada, sem exceção sem tratamento.

Os relatórios da primeira captura começam com `11 FF` e têm 20 bytes, formato compatível com o pacote longo HID++ 2.0 descrito pela [documentação pública da Logitech](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst). Essa semelhança não identifica a função dos relatórios. A documentação lista a feature `0x1814` como Change Host, mas ainda não foi mapeado o índice dessa feature nestes dispositivos.

Resultado: a presença de `B377` no inventário HID permite detectar sua saída e seu retorno neste host. A ausência isolada não identifica o canal de destino nem comprova que exista um relatório HID prévio à desconexão. Próxima verificação: estudar a informação de host disponível enquanto o teclado ainda está conectado e estabelecer como associar o canal escolhido ao mouse, sem escrita HID nesta etapa.

## 2026-09-25 — primeira captura na bancada WPF

A sessão visual de `B034 FF43/0202` começou às `10:18:43.455`. A janela registrou ausências às `10:18:47.584`, `10:19:09.261` e `10:19:49.724`, e retornos às `10:19:02.079` e `10:19:41.493`. Nos dois retornos, a leitura foi reaberta automaticamente na mesma interface e recebeu relatórios. A sessão foi parada às `10:19:52.076`, com 142 relatórios e aproximadamente 1 minuto e 8 segundos de duração. A exportação pela janela confirmou “Registro salvo”.

Uma cópia local dos registros está em `artifacts/captura-mouse-20260925.txt` (diretório ignorado pelo Git). Esses dados comprovam o funcionamento da bancada, incluindo ausência, reconexão e parada. A sequência física dos canais não foi anotada nesta sessão e os relatórios ainda não foram decodificados.
