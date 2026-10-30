# H3 Monitor

Laboratório digital para estudar risco higrotérmico em ativos industriais. O
projeto simula condições de clima quente e úmido, calcula ponto de orvalho,
margem de condensação, exposição H3 e estatísticas de risco.

## Entregas do MVP digital

- cenários sintéticos reproduzíveis por semente;
- temperatura e umidade externas e internas;
- temperatura superficial simulada;
- cálculo de ponto de orvalho;
- classificação experimental de risco;
- CSV para análise estatística;
- resumo JSON;
- dashboard HTML totalmente offline;
- experimento Monte Carlo pareado de baixo consumo;
- comparação de cinco estratégias sintéticas com IC95% aproximado;
- relatório de pesquisa em Markdown, JSON e CSV, sem depender de HTML;
- importação de dados anuais oficiais do INMET por HTTP Range;
- extração segura de uma estação sem baixar o ZIP anual inteiro;
- proveniência com ETag, Last-Modified, SHA-256, UTC e qualidade;
- comparação prudente entre clima externo real e entradas sintéticas;
- testes executáveis sem serviços externos.

## Limitações

- Os dados INMET representam clima externo bruto/não consistido; não são
  medições dentro de painéis ou equipamentos.
- As simulações continuam **sintéticas** e não comprovam comportamento de uma
  planta real.
- Os limites de risco são hipóteses de engenharia para experimentação, não
  limites normativos.
- O programa não comanda climatização, painéis ou equipamentos.
- Correlação entre condição ambiental e ocorrência não confirma causalidade.

## Executar

Requer .NET 8 SDK ou superior.

```powershell
.\Executar-H3Monitor.ps1 -Days 90 -IntervalMinutes 10 -Seed 20261029
```

Os resultados ficam em `output\demo`:

- `h3-synthetic.csv`;
- `h3-summary.json`;
- `h3-dashboard.html`.

## Validar

```powershell
.\Validar-H3Monitor.ps1
```

## Executar pesquisa programacional

O protocolo usa a mesma realização climática em cada par referência/intervenção.
Assim, a comparação exige menos replicações do que cenários independentes e
permanece reproduzível pela semente.

```powershell
.\Executar-PesquisaH3.ps1 -Replications 60 -Days 30 -IntervalMinutes 30
```

Os resultados leves ficam em `output\research`:

- `RELATORIO_PESQUISA_H3.md` para leitura direta;
- `h3-experiment-summary.json` para auditoria e integração;
- `h3-experiment-observations.csv` para análise estatística.

O experimento compara controle de umidade, margem térmica superficial, redução
de ingresso de ar úmido, confiabilidade do HVAC e estratégia combinada. Seus
intervalos de confiança são aproximações do comportamento do modelo, não
incerteza de uma planta real.

O desenho científico e os critérios para avançar de simulação a campo estão em
[`docs/PROTOCOLO_DE_EVIDENCIA.md`](docs/PROTOCOLO_DE_EVIDENCIA.md).

## Coletar clima oficial do INMET

O modo INMET descobre o diretório central do ZIP remoto, transfere somente a
entrada da estação e rejeita servidores que ignorem HTTP Range. Por padrão usa
Manaus A101/2025, mas aceita outro ano e código.

```powershell
.\Coletar-INMET.ps1 -Year 2025 -Station A101
```

A saída `output\inmet-2025-A101` contém o CSV bruto imutável, resumo JSON e
relatório Markdown. Nenhum HTML é criado. Ausências `9999`, `Null` e vazios são
mantidas como ausentes, sem imputação.

Na execução verificada de A101/2025, foram transferidos 349.533 bytes de um ZIP
de 90.898.634 bytes. O resultado e sua interpretação responsável estão em
[`docs/ESTUDO_INMET_A101_2025.md`](docs/ESTUDO_INMET_A101_2025.md).

## Evolução planejada

1. Recalibrar as distribuições externas sintéticas sem ajustar o modelo aos
   próprios resultados que se deseja demonstrar.
2. Importador de histórico CMMS anonimizado.
3. Nó físico com T/RH ambiente e temperatura superficial.
4. MQTT com TLS e alternativa Modbus TCP.
5. Validação em bancada desenergizada.
6. Piloto de campo somente leitura e aprovado por segurança.
