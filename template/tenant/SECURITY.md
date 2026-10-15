# Política de segurança

Use **Report a vulnerability** na aba Security deste repositório (GitHub Private Vulnerability Reporting). Não abra
issue pública para vulnerabilidades.

- Confirmação em até 3 dias úteis; avaliação de severidade em até 7 dias; correção ou mitigação para alta/crítica
  em até 30 dias.
- Segredos nunca entram neste repositório: são sincronizados do cofre pelo External Secrets Operator. Se um
  segredo vazar num commit, rotacione-o no cofre primeiro, depois limpe o histórico.
- Promoção para produção só por PR aprovada por `@__AEDIS_ORG__/platform-security`.
