# Política de segurança

Use **Report a vulnerability** na aba Security deste repositório (GitHub Private Vulnerability Reporting). Não abra
issue pública para vulnerabilidades.

- Confirmação em até 3 dias úteis; avaliação de severidade em até 7 dias; correção ou mitigação para alta/crítica
  em até 30 dias.
- Segredos nunca entram neste repositório. O site não tem segredos de banco nem de fila: a sessão fica em cookie
  `HttpOnly` emitido pelo backend e o backend é chamado só na mesma origem (`api()` do Aedis UI).
- Os cabeçalhos de segurança (CSP sem script inline fora do nonce ou dos hashes, HSTS, sem framing) saem da própria
  app em toda resposta de página: `proxy.ts`, ou o `_headers` gerado no build de um site estático.
