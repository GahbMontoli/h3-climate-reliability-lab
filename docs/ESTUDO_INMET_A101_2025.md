# Estudo climático reproduzível — INMET Manaus A101/2025

## Resultado executivo

O importador encontrou e extraiu somente a estação A101 dentro do ZIP anual
oficial do INMET. Foram transferidos **349.533 bytes de 90.898.634 bytes**
(0,385% do pacote), preservando o CSV bruto e sua proveniência.

Esse resultado acrescenta uma base climática externa real à pesquisa H3, mas
**não comprova condensação interna, falha de equipamento ou causalidade**.

## Proveniência

- Fonte: `https://portal.inmet.gov.br/uploads/dadoshistoricos/2025.zip`
- Entrada: `2025/INMET_N_AM_A101_MANAUS_01-01-2025_A_31-12-2025.CSV`
- Tamanho do CSV: 836.932 bytes
- SHA-256: `0892c3ab072d475ff59ff82948154e3b853f3b7e32007c84d27eda642c1e12e7`
- Fuso: UTC
- Qualidade: `raw_unvalidated` — dado automático bruto/não consistido
- Tratamento de ausências: vazio, `9999` e `Null` permanecem ausentes
- Imputação: nenhuma

## Cobertura e estatísticas

- Período: 01/01/2025 00:00 UTC a 31/12/2025 23:00 UTC
- Cobertura temporal: 8.760 de 8.760 horas, sem timestamps duplicados
- Temperatura: 8.687 válidos; 73 ausentes; P05 23,7 °C; mediana 27,1 °C;
  P95 32,6 °C
- Umidade relativa: 8.684 válidos; 76 ausentes; P05 56%; mediana 81%;
  P95 96%
- Ponto de orvalho externo: 8.680 válidos; 80 ausentes; P05 21,6 °C;
  mediana 23,4 °C; P95 24,8 °C

## O que o dado real mostrou sobre o modelo

Comparando somente variáveis **externas**, o ponto de orvalho sintético ficou
deslocado para cima: mediana de 25,573 °C no modelo contra 23,4 °C no INMET. Ao
mesmo tempo, 55,818% das observações reais de ponto de orvalho ficaram fora do
intervalo P05–P95 sintético.

Isso não invalida a hipótese H3. Mostra que o gerador externo atual não
representa toda a distribuição observada em Manaus e deve ser recalibrado antes
de qualquer estimativa quantitativa de exposição. O resultado contrário à
aderência perfeita foi mantido, em vez de ajustar parâmetros após observar a
resposta.

## Reproduzir

```powershell
.\Coletar-INMET.ps1 -Year 2025 -Station A101
.\Validar-H3Monitor.ps1
```

A validação usa fixture ZIP/CSV local e não depende da rede. A coleta real exige
que a origem oficial aceite HTTP Range; uma resposta integral é bloqueada.
