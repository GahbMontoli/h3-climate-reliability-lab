# Modelo sintético do H3 Monitor

## Finalidade

O modelo produz dados controlados para desenvolver cálculos, relatórios e
testes antes da instrumentação física. Ele não é uma reprodução validada do
clima de Manaus nem de um equipamento específico.

## Variáveis

- temperatura e umidade relativa externas;
- temperatura e umidade relativa internas;
- temperatura superficial;
- ponto de orvalho;
- margem entre superfície e ponto de orvalho;
- abertura de porta;
- estado da climatização;
- cenário e nível de risco.

## Ponto de orvalho

O cálculo usa uma aproximação de Magnus com `a = 17,62` e `b = 243,12 °C`:

```text
gamma = ln(RH / 100) + (a * T) / (b + T)
Td = (b * gamma) / (a - gamma)
```

O indicador físico principal é:

```text
margem = temperatura_superficial - ponto_de_orvalho
```

## Limites experimentais

| Margem | Nível |
| ---: | --- |
| `> 5 °C` | Normal |
| `> 3 °C` e `<= 5 °C` | Atenção |
| `> 1 °C` e `<= 3 °C` | Elevado |
| `> 0 °C` e `<= 1 °C` | Crítico |
| `<= 0 °C` | Condensação provável |

Esses limites são hipóteses do laboratório e precisam ser calibrados com
incerteza de medição, tipo de ativo, resposta térmica, contaminação e dados de
campo. Não são extraídos de uma norma.

## Cenários gerados

- `baseline`: climatização disponível e ambiente interno controlado;
- `humid-air-ingress`: abertura programada ou eventual de porta;
- `hvac-outage`: indisponibilidade programada no cenário sintético.

A semente aleatória garante repetibilidade. A mesma configuração deve gerar a
mesma série, permitindo comparar alterações do algoritmo.

## Estatísticas

- distribuição de amostras por nível;
- horas com risco elevado ou superior;
- horas críticas;
- quantidade de eventos contínuos;
- maior duração de evento;
- correlação de Pearson entre umidade externa e margem.

A correlação é descritiva e não demonstra causalidade. Antes de qualquer
conclusão científica, serão necessários dados reais, avaliação de qualidade,
controle de confundidores e protocolo experimental registrado.

## Próxima validação

1. Comparar o cálculo com uma biblioteca ou instrumento de referência.
2. Importar série oficial do INMET mantendo fonte e horário.
3. Executar ensaio em bancada desenergizada.
4. Registrar umidade superficial ou evidência visual de condensação.
5. Estimar falso positivo, falso negativo e incerteza combinada.
