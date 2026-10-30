# Protocolo de evidência H3

Este protocolo separa coerência programacional, associação estatística e
evidência causal. O objetivo é impedir que uma boa simulação seja apresentada
como comprovação de campo.

## Hipóteses testáveis

- **H1 sintética:** reduzir ingresso de ar úmido ou elevar a margem térmica
  superficial reduz horas com margem para o ponto de orvalho menor ou igual a
  3 °C dentro do modelo.
- **H0 sintética:** a diferença pareada média de exposição é zero.
- **H1 de campo futura:** maior exposição H3 antecede aumento de falhas
  compatíveis com umidade, após controlar idade, carga, intervenção, ambiente e
  família do ativo.

O experimento atual testa somente a primeira hipótese. Um IC95% que inclui zero
é tratado como efeito não distinguido pelo modelo, sem procurar uma conclusão
positiva à força.

## Escada de comprovação

1. Testes automatizados verificam fórmulas, determinismo e arquivos.
2. Simulações pareadas verificam a coerência interna e priorizam variáveis.
3. Dados históricos oficiais verificam se o envelope ambiental simulado é
   plausível para Manaus. **Executado para INMET A101/2025; a divergência foi
   preservada como resultado, sem recalibração retroativa nesta etapa.**
4. Bancada desenergizada compara sensores e eventos controlados de ingresso de
   umidade.
5. Piloto somente leitura associa exposição ambiental e histórico de falhas.
6. Somente depois, um estudo prospectivo aprovado pode avaliar efeito causal de
   uma intervenção.

## Proveniência e qualidade para dados INMET

A ingestão parte dos [Dados Históricos Anuais do INMET](https://portal.inmet.gov.br/dadoshistoricos/)
e registra, em cada lote:

- URL e nome do arquivo de origem;
- estação, código, latitude, longitude, altitude e período;
- hash SHA-256 do arquivo bruto;
- data de importação e versão do importador;
- fuso original UTC e regra explícita de conversão;
- valores ausentes `9999`, `Null` ou vazios, sem imputação silenciosa;
- flag `raw_unvalidated` para dados automáticos brutos/não consistidos;
- contagem de linhas aceitas, rejeitadas e ausentes por variável.

O arquivo bruto deve permanecer imutável. Correções, filtros e imputações serão
camadas derivadas e auditáveis.

## Critérios antes de divulgar resultados

- semente, parâmetros e versão do código registrados;
- resultado reproduzido por execução limpa;
- dados sintéticos e reais claramente separados;
- intervalo de confiança e tamanho da amostra informados;
- resultados nulos e contrários à hipótese mantidos;
- nenhuma correlação descrita como causa;
- limitações técnicas e operacionais explícitas.
