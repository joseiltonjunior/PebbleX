# Protocolo

## Fatos documentados

A [documentação pública HID++ 2.0 da Logitech](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst) descreve pacotes curtos com report ID `0x10` e 7 bytes, e pacotes longos com report ID `0x11` e 20 bytes. O segundo byte é o índice do dispositivo; `0xFF` é reservado para dispositivos com fio e receptores. A lista de features inclui `0x1814` (Change Host).

O índice de uma feature dentro de um dispositivo precisa ser descoberto na tabela desse dispositivo. A consulta usa ROOT no índice zero e correlaciona device index, feature index e function/software ID. Na interface Bluetooth observada, o dispositivo respondeu ao índice `FF`. As capturas e suas limitações estão em [OBSERVATIONS.md](OBSERVATIONS.md).

## Consultas confirmadas em hardware (25/09/2026)

- `1814` ChangeHost: ROOT retornou índice `0A`, versão 1, no B377 e B034. A função zero retornou quantidade de hosts e host atual com índice iniciado em zero. O teclado retornou `03 00` (canal 1); o mouse retornou `03 02` (canal 3).
- `1B04` ReprogControls: B377 retornou índice `09`, versão 5, e 15 controles. Os CIDs `00D1`, `00D2`, `00D3` retornaram flags `040A`: capacidade de evento analítico presente (`0400`), desvio comum ausente (`0020`). Isso confirma uma capacidade anunciada; ainda não confirma que a notificação chegará antes da desconexão.
- A bancada pode ativar/desativar o reporting analítico temporário dos CIDs D1/D2/D3 e agora oferece uma solicitação manual de `ChangeHost.setCurrentHost` somente para o mouse B034. O comando é enviado sem aguardar resposta: a troca derruba a conexão HID. A interface só deve indicar o canal após uma nova consulta no destino.
- O comando `ChangeHost.setCurrentHost` foi validado no B034 em hardware: o relatório é enviado sem esperar resposta HID porque a conexão cai durante a troca. A confirmação depende de reconexão e consulta `getHostInfo` no canal de destino. O teclado permanece como gatilho por chegada/reconexão; ainda não se provou que os relatórios analíticos D1/D2/D3 informem o destino antes da desconexão.

## Pareamento e Flow pela rede local

- A descoberta UDP `47630` anuncia ID de instalação, nome e portas como dados não confiáveis; um anúncio nunca autoriza controle.
- O pareamento usa TCP `47631`, ECDH P-256 efêmero, derivação HKDF-SHA256 e código de verificação de oito dígitos. As instalações exibem o mesmo código e cada lado exige consentimento. A chave resultante fica localmente protegida por DPAPI; o protocolo não envia a chave derivada.
- O Flow usa TCP autenticado por HMAC-SHA256 em `47631`, com identificador de transferência, validade e deduplicação em memória. Pareamento e listener Flow usam a porta em modos exclusivos.
- A troca do mouse e o fluxo ponta a ponta foram confirmados em hardware nos dois sentidos entre os PCs Windows. O usuário também confirmou descoberta e pareamento automáticos nos PCs reais. Medição de latência e testes de estabilidade continuam pendentes.

O formato do retorno de ChangeHost e da tabela de controles foi conferido nas implementações primárias do [Solaar: ChangeHost](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/settings_templates.py) e [Solaar: KeysArrayV4 e KeyFlag](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py). A implementação local é independente e limitada ao diagnóstico. Não confundir o bit `04` do primeiro byte de flags (NONSTANDARD) com `04` do segundo (ANALYTICS_KEY_EVENTS).
