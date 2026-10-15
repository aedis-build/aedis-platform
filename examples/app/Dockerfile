# Imagem de exemplo para o dogfooding do build-image.yml (assinatura, SBOM, proveniência, Trivy): o agnhost dos
# testes do Kubernetes servindo HTTP em 8080 com /healthz, como non-root. Não é uma app Aedis; o Dockerfile de
# uma app Aedis é o do template aedis-api (sdk -> aspnet chiseled).
FROM registry.k8s.io/e2e-test-images/agnhost@sha256:cc249acbd34692826b2b335335615e060fdb3c0bca4954507aa3a1d1194de253
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["/agnhost", "netexec", "--http-port=8080"]
