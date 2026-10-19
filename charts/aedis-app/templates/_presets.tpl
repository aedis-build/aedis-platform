{{/*
Presets de fornecedor. Regra: um preset só ADICIONA labels, anotações ou env; nunca altera o baseline
de segurança nem o restante do pod. Com observability.vendor.name=none e ingress.preset=nginx o chart
não contém nada de fornecedor.
*/}}

{{/* Valor de "env" na visão do fornecedor (ex.: Datadog DD_ENV): options.env ou o environment. */}}
{{- define "aedis.vendor.envName" -}}
{{- default (include "aedis.environment" .) (get .Values.observability.vendor.options "env") -}}
{{- end -}}

{{- define "aedis.vendor.podLabels" -}}
{{- $v := .Values.observability.vendor -}}
{{- if eq $v.name "datadog" }}
tags.datadoghq.com/service: {{ include "aedis.name" . | quote }}
tags.datadoghq.com/env: {{ include "aedis.vendor.envName" . | quote }}
tags.datadoghq.com/version: {{ include "aedis.version" . | quote }}
{{- else if eq $v.name "newrelic" }}
newrelic.io/service: {{ include "aedis.name" . | quote }}
{{- end }}
{{- end -}}

{{- define "aedis.vendor.podAnnotations" -}}
{{- $v := .Values.observability.vendor -}}
{{- if eq $v.name "dynatrace" }}
{{- $opts := $v.options }}
oneagent.dynatrace.com/inject: "true"
{{- with (get $opts "technologies") }}
oneagent.dynatrace.com/technologies: {{ . | quote }}
{{- end }}
{{- else if eq $v.name "splunk" }}
{{- with (get $v.options "instrumentation") }}
instrumentation.opentelemetry.io/inject-dotnet: {{ . | quote }}
{{- end }}
{{- end }}
{{- end -}}

{{- define "aedis.vendor.env" -}}
{{- $v := .Values.observability.vendor -}}
{{- if eq $v.name "datadog" }}
- name: DD_SERVICE
  valueFrom:
    fieldRef:
      fieldPath: metadata.labels['tags.datadoghq.com/service']
- name: DD_ENV
  valueFrom:
    fieldRef:
      fieldPath: metadata.labels['tags.datadoghq.com/env']
- name: DD_VERSION
  valueFrom:
    fieldRef:
      fieldPath: metadata.labels['tags.datadoghq.com/version']
- name: DD_AGENT_HOST
  valueFrom:
    fieldRef:
      fieldPath: status.hostIP
{{- else if eq $v.name "newrelic" }}
- name: NEW_RELIC_APP_NAME
  value: {{ include "aedis.name" . | quote }}
{{- else if eq $v.name "dynatrace" }}
- name: DT_RELEASE_VERSION
  value: {{ include "aedis.version" . | quote }}
- name: DT_RELEASE_STAGE
  value: {{ include "aedis.vendor.envName" . | quote }}
{{- end }}
{{- end -}}

{{/* ---- Ingress presets: cada um devolve {className, annotations} em YAML. ---- */}}

{{- define "aedis.ingress.preset.nginx" -}}
className: nginx
annotations:
  nginx.ingress.kubernetes.io/ssl-redirect: "true"
  nginx.ingress.kubernetes.io/force-ssl-redirect: "true"
  nginx.ingress.kubernetes.io/proxy-body-size: {{ .Values.ingress.nginx.proxyBodySize | quote }}
{{- end -}}

{{- define "aedis.ingress.preset.alb" -}}
{{- $a := .Values.ingress.alb -}}
{{- $hc := $a.healthcheck -}}
{{- $tg := list (printf "deregistration_delay.timeout_seconds=%v" $a.deregistrationDelaySeconds) "load_balancing.algorithm.type=least_outstanding_requests" -}}
className: alb
annotations:
  alb.ingress.kubernetes.io/scheme: {{ $a.scheme }}
  alb.ingress.kubernetes.io/target-type: ip
  alb.ingress.kubernetes.io/ip-address-type: ipv4
  alb.ingress.kubernetes.io/listen-ports: '[{"HTTP": 80}, {"HTTPS": 443}]'
  alb.ingress.kubernetes.io/ssl-redirect: "443"
  alb.ingress.kubernetes.io/ssl-policy: {{ $a.sslPolicy }}
  alb.ingress.kubernetes.io/healthcheck-protocol: HTTP
  alb.ingress.kubernetes.io/healthcheck-path: {{ $hc.path | default (include "aedis.probePath" (dict "root" . "path" .Values.probes.readiness.path)) }}
  alb.ingress.kubernetes.io/healthcheck-port: {{ .Values.container.port | quote }}
  alb.ingress.kubernetes.io/healthcheck-interval-seconds: {{ $hc.intervalSeconds | quote }}
  alb.ingress.kubernetes.io/healthcheck-timeout-seconds: {{ $hc.timeoutSeconds | quote }}
  alb.ingress.kubernetes.io/healthy-threshold-count: {{ $hc.healthyThreshold | quote }}
  alb.ingress.kubernetes.io/unhealthy-threshold-count: {{ $hc.unhealthyThreshold | quote }}
  alb.ingress.kubernetes.io/success-codes: {{ $hc.successCodes | quote }}
  alb.ingress.kubernetes.io/target-group-attributes: {{ join "," $tg | quote }}
  {{- with $a.groupName }}
  alb.ingress.kubernetes.io/group.name: {{ . }}
  {{- end }}
  {{- with $a.certificateArn }}
  alb.ingress.kubernetes.io/certificate-arn: {{ . }}
  {{- end }}
  {{- with $a.subnets }}
  alb.ingress.kubernetes.io/subnets: {{ join "," . }}
  {{- end }}
  {{- with $a.inboundCidrs }}
  alb.ingress.kubernetes.io/inbound-cidrs: {{ join "," . }}
  {{- end }}
  {{- with .Values.ingress.hosts }}
  external-dns.alpha.kubernetes.io/hostname: {{ join "," . }}
  {{- end }}
{{- end -}}

{{- define "aedis.ingress.preset.agic" -}}
{{- $g := .Values.ingress.agic -}}
className: azure-application-gateway
annotations:
  appgw.ingress.kubernetes.io/ssl-redirect: "true"
  appgw.ingress.kubernetes.io/backend-protocol: {{ $g.backendProtocol }}
  appgw.ingress.kubernetes.io/health-probe-path: {{ (include "aedis.probePath" (dict "root" . "path" .Values.probes.readiness.path)) }}
  appgw.ingress.kubernetes.io/health-probe-status-codes: "200"
  {{- with $g.sslCertificate }}
  appgw.ingress.kubernetes.io/appgw-ssl-certificate: {{ . }}
  {{- end }}
{{- end -}}

{{/* DigitalOcean: o LB da DO é anotado no Service do ingress-nginx, não no Ingress da app. Mesmo preset do nginx. */}}
{{- define "aedis.ingress.preset.do-nginx" -}}
{{ include "aedis.ingress.preset.nginx" . }}
{{- end -}}

{{- define "aedis.ingress.preset.none" -}}
className: ""
annotations: {}
{{- end -}}

{{/* Resolve o preset escolhido (kong tem template próprio). */}}
{{- define "aedis.ingress.preset" -}}
{{- $p := .Values.ingress.preset -}}
{{- if eq $p "nginx" -}}{{ include "aedis.ingress.preset.nginx" . }}
{{- else if eq $p "alb" -}}{{ include "aedis.ingress.preset.alb" . }}
{{- else if eq $p "agic" -}}{{ include "aedis.ingress.preset.agic" . }}
{{- else if eq $p "do-nginx" -}}{{ include "aedis.ingress.preset.do-nginx" . }}
{{- else if eq $p "none" -}}{{ include "aedis.ingress.preset.none" . }}
{{- else if eq $p "kong" -}}{{ include "aedis.ingress.preset.none" . }}
{{- else -}}{{ fail (printf "ingress.preset '%s' desconhecido (nginx | alb | agic | kong | do-nginx | none)" $p) }}{{- end -}}
{{- end -}}
