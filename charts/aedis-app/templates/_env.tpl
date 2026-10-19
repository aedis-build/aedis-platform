{{/*
Variáveis de ambiente do container. O chart deriva o que configura o host Aedis a partir dos
values (framework.enabled); container.env e framework.env são mesclados por último e vencem.
*/}}
{{- define "aedis.aspnetEnvironment" -}}
{{- if .Values.framework.aspnetEnvironment -}}{{ .Values.framework.aspnetEnvironment }}
{{- else if include "aedis.isProd" . -}}Production
{{- else -}}Staging{{- end -}}
{{- end -}}

{{/* Hosts aceitos pelo HostHeaders: ingress.hosts + framework.allowedHosts + o Service interno. */}}
{{- define "aedis.allowedHosts" -}}
{{- $name := include "aedis.name" . -}}
{{- $ns := include "aedis.namespace" . -}}
{{- $hosts := list $name (printf "%s.%s" $name $ns) (printf "%s.%s.svc" $name $ns) (printf "%s.%s.svc.cluster.local" $name $ns) -}}
{{- if .Values.ingress.enabled -}}{{- $hosts = concat $hosts .Values.ingress.hosts -}}{{- end -}}
{{- $hosts = concat $hosts .Values.framework.allowedHosts -}}
{{- $hosts | uniq | toJson -}}
{{- end -}}

{{- define "aedis.proxyNetworks" -}}
{{- $nets := .Values.framework.proxyNetworks -}}
{{- if not $nets -}}{{- $nets = .Values.ingress.proxyNetworks -}}{{- end -}}
{{- $nets | toJson -}}
{{- end -}}

{{- define "aedis.resourceAttributes" -}}
{{- $attrs := dict
  "deployment.environment" (include "aedis.environment" .)
  "deployment.environment.class" .Values.environmentClass
  "service.namespace" (include "aedis.namespace" .)
  "service.version" (include "aedis.version" .)
  "k8s.namespace.name" (include "aedis.namespace" .)
  "k8s.pod.name" "$(POD_NAME)"
  "k8s.container.name" (include "aedis.containerName" .)
  "cloud.provider" .Values.cloud.provider
-}}
{{- with .Values.cloud.region }}{{- $_ := set $attrs "cloud.region" . }}{{- end -}}
{{- range $k, $v := .Values.observability.resourceAttributes }}{{- $_ := set $attrs $k (toString $v) }}{{- end -}}
{{- $pairs := list -}}
{{- range $k, $v := $attrs }}{{- $pairs = append $pairs (printf "%s=%s" $k $v) }}{{- end -}}
{{- join "," $pairs -}}
{{- end -}}

{{- define "aedis.frameworkEnv" -}}
{{- $env := dict -}}
{{- $_ := set $env "ASPNETCORE_ENVIRONMENT" (include "aedis.aspnetEnvironment" .) -}}
{{- $_ := set $env "DOTNET_ENVIRONMENT" (include "aedis.aspnetEnvironment" .) -}}
{{- $_ := set $env "ASPNETCORE_URLS" (printf "http://+:%d" (int .Values.container.port)) -}}
{{- $_ := set $env "OTEL_SERVICE_NAME" (include "aedis.name" .) -}}
{{- $_ := set $env "OTEL_RESOURCE_ATTRIBUTES" (include "aedis.resourceAttributes" .) -}}
{{- $o := .Values.observability.otlp -}}
{{- if $o.endpoint -}}
{{- $_ := set $env "Telemetry__OtlpEndpoint" $o.endpoint -}}
{{- $_ := set $env "OTEL_EXPORTER_OTLP_ENDPOINT" $o.endpoint -}}
{{- $_ := set $env "OTEL_EXPORTER_OTLP_PROTOCOL" $o.protocol -}}
{{- $_ := set $env "Telemetry__EnableMetrics" (toString $o.metrics) -}}
{{- $_ := set $env "Telemetry__EnableTraces" (toString $o.traces) -}}
{{- $_ := set $env "Telemetry__EnableLogs" (toString $o.logs) -}}
{{- end -}}
{{- if eq .Values.type "api" -}}
{{- $_ := set $env "Security__Https__EnableHttpsRedirection" "false" -}}
{{- $_ := set $env "Security__Https__EnableHsts" (ternary "true" "false" (eq (include "aedis.isProd" .) "true")) -}}
{{- $_ := set $env "Security__ForwardedHeaders__TrustAllProxies" "false" -}}
{{- $nets := include "aedis.proxyNetworks" . | fromJsonArray -}}
{{- range $i, $n := $nets }}{{- $_ := set $env (printf "Security__ForwardedHeaders__KnownNetworks__%d" $i) $n }}{{- end -}}
{{- $hosts := include "aedis.allowedHosts" . | fromJsonArray -}}
{{- range $i, $h := $hosts }}{{- $_ := set $env (printf "Security__HostHeaders__AllowedHosts__%d" $i) $h }}{{- end -}}
{{- end -}}
{{- $r := .Values.secrets.runtime -}}
{{- if $r.enabled -}}
{{- with $r.awsRegion }}{{- $_ := set $env "AwsSecretsManager__Region" . }}{{- end -}}
{{- with $r.azureVaultUri }}{{- $_ := set $env "AzureKeyVault__VaultUri" . }}{{- end -}}
{{- with $r.vaultAddress }}{{- $_ := set $env "Vault__Address" . }}{{- end -}}
{{- end -}}
{{- range $k, $v := .Values.framework.env }}{{- $_ := set $env $k (toString $v) }}{{- end -}}
{{- range $k, $v := $env }}
- name: {{ $k }}
  value: {{ $v | quote }}
{{- end -}}
{{- end -}}

{{- define "aedis.env" -}}
- name: POD_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
- name: POD_NAMESPACE
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: POD_INSTANCE_ID
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
{{- if and .Values.framework.enabled (ne .Values.type "site") }}
{{- include "aedis.frameworkEnv" . }}
{{- end }}
{{- if and .Values.observability.otlp.endpoint .Values.observability.otlp.apiKeySecretKey }}
- name: Telemetry__OtlpApiKey
  valueFrom:
    secretKeyRef:
      name: {{ include "aedis.name" . }}-secret
      key: {{ .Values.observability.otlp.apiKeySecretKey }}
{{- end }}
{{- include "aedis.vendor.env" . }}
{{- with .Values.container.env }}
{{ toYaml . }}
{{- end }}
{{- end -}}
