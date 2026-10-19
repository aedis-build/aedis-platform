{{- define "aedis.name" -}}{{ required "name é obrigatório" .Values.name }}{{- end -}}

{{- define "aedis.namespace" -}}{{ required "namespace é obrigatório" .Values.namespace }}{{- end -}}

{{- define "aedis.environment" -}}{{ required "environment é obrigatório (env/<env>.yaml)" .Values.environment }}{{- end -}}

{{- define "aedis.isProd" -}}{{- if eq .Values.environmentClass "prod" -}}true{{- end -}}{{- end -}}

{{/* Labels de seleção: estáveis, nunca mudam entre releases. */}}
{{- define "aedis.selectorLabels" -}}
app.kubernetes.io/name: {{ include "aedis.name" . }}
app.kubernetes.io/component: {{ .Values.type }}
{{- end -}}

{{/* Labels de metadados de todos os objetos. */}}
{{- define "aedis.labels" -}}
{{ include "aedis.selectorLabels" . }}
app.kubernetes.io/part-of: aedis
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/version: {{ include "aedis.version" . | quote }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
aedis.build/environment: {{ include "aedis.environment" . }}
aedis.build/environment-class: {{ .Values.environmentClass }}
{{- with .Values.owner }}
aedis.build/owner: {{ . }}
{{- end }}
{{- end -}}

{{/* Labels do pod: as de seleção + versão + presets de observabilidade. */}}
{{- define "aedis.podLabels" -}}
{{ include "aedis.selectorLabels" . }}
app.kubernetes.io/version: {{ include "aedis.version" . | quote }}
aedis.build/environment: {{ include "aedis.environment" . }}
{{- if and .Values.identity.enabled (eq .Values.cloud.provider "azure") }}
azure.workload.identity/use: "true"
{{- end }}
{{- include "aedis.vendor.podLabels" . }}
{{- end -}}

{{/* Versão exibida (tag, ou os 12 primeiros hex do digest quando não há tag). */}}
{{- define "aedis.version" -}}
{{- if .Values.image.tag -}}{{ .Values.image.tag }}
{{- else if .Values.image.digest -}}{{ trimPrefix "sha256:" .Values.image.digest | trunc 12 }}
{{- else -}}unknown{{- end -}}
{{- end -}}

{{/* Referência da imagem: repo@digest quando há digest (obrigatório em prod, L2+), senão repo:tag. */}}
{{- define "aedis.image" -}}
{{- $repo := required "image.repository é obrigatório" .Values.image.repository -}}
{{- if .Values.image.digest -}}
{{ $repo }}@{{ .Values.image.digest }}
{{- else -}}
{{ $repo }}:{{ required "image.tag é obrigatório quando não há image.digest" .Values.image.tag }}
{{- end -}}
{{- end -}}

{{- define "aedis.containerName" -}}
{{- if .Values.container.name -}}{{ .Values.container.name }}
{{- else if eq .Values.type "cronjob" -}}job
{{- else -}}{{ .Values.type }}{{- end -}}
{{- end -}}

{{/* api e site são servidos a usuários: Service HTTP, Ingress, piso de réplicas e análise HTTP do canário. */}}
{{- define "aedis.isServed" -}}
{{- if has .Values.type (list "api" "site") -}}true{{- end -}}
{{- end -}}

{{- define "aedis.portName" -}}
{{- if .Values.container.portName -}}{{ .Values.container.portName }}
{{- else if include "aedis.isServed" . -}}http
{{- else -}}health{{- end -}}
{{- end -}}

{{- define "aedis.serviceAccountName" -}}
{{- default (include "aedis.name" .) .Values.identity.serviceAccountName -}}
{{- end -}}

{{- define "aedis.autoscaled" -}}
{{- if or .Values.autoscaling.hpa.enabled .Values.autoscaling.keda.enabled -}}true{{- end -}}
{{- end -}}

{{- define "aedis.isCanary" -}}
{{- if and (ne .Values.type "cronjob") (eq .Values.rollout.strategy "canary") -}}true{{- end -}}
{{- end -}}

{{/* Provider de segredos efetivo: secrets.provider ou derivado de cloud.provider. */}}
{{- define "aedis.secretsProvider" -}}
{{- if .Values.secrets.provider -}}{{ .Values.secrets.provider }}
{{- else if eq .Values.cloud.provider "aws" -}}aws
{{- else if eq .Values.cloud.provider "azure" -}}azure
{{- else -}}vault{{- end -}}
{{- end -}}

{{/* ARN da role IRSA: roleArn, ou montado de accountId + roleName. */}}
{{- define "aedis.awsRoleArn" -}}
{{- $a := .Values.identity.aws -}}
{{- if $a.roleArn -}}{{ $a.roleArn }}
{{- else -}}
arn:aws:iam::{{ required "identity.aws.accountId (ou roleArn) é obrigatório com identity.enabled em aws" $a.accountId }}:role/{{ default (include "aedis.name" .) $a.roleName }}
{{- end -}}
{{- end -}}

{{- define "aedis.podSecurityContext" -}}
runAsNonRoot: true
runAsUser: {{ .Values.security.uid }}
runAsGroup: {{ .Values.security.gid }}
fsGroup: {{ .Values.security.gid }}
fsGroupChangePolicy: OnRootMismatch
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "aedis.containerSecurityContext" -}}
allowPrivilegeEscalation: false
privileged: false
readOnlyRootFilesystem: true
runAsNonRoot: true
runAsUser: {{ .Values.security.uid }}
runAsGroup: {{ .Values.security.gid }}
capabilities:
  drop:
    - ALL
{{- end -}}

{{/* Um site serve páginas, não /health/*: os caminhos padrão do chart viram "/"; outro caminho explícito é respeitado. */}}
{{- define "aedis.probePath" -}}
{{- if and (eq .root.Values.type "site") (has .path (list "/health/live" "/health/ready")) -}}/{{- else -}}{{ .path }}{{- end -}}
{{- end -}}

{{- define "aedis.probe" -}}
httpGet:
  path: {{ include "aedis.probePath" (dict "root" .root "path" .p.path) }}
  port: {{ .port }}
  scheme: HTTP
initialDelaySeconds: {{ .p.initialDelaySeconds }}
periodSeconds: {{ .p.periodSeconds }}
timeoutSeconds: {{ .p.timeoutSeconds }}
failureThreshold: {{ .p.failureThreshold }}
{{- if not .startup }}
successThreshold: 1
{{- end }}
{{- end -}}

{{/* Piso de réplicas: só api e site. */}}
{{- define "aedis.replicaFloor" -}}
{{- if include "aedis.isServed" . -}}{{ int .Values.availability.apiMinReplicas }}{{- else -}}0{{- end -}}
{{- end -}}

{{- define "aedis.replicas" -}}
{{- max (int .Values.replicas) (include "aedis.replicaFloor" . | int) -}}
{{- end -}}

{{- define "aedis.hpaMinReplicas" -}}
{{- max (int .Values.autoscaling.hpa.minReplicas) (include "aedis.replicaFloor" . | int) -}}
{{- end -}}

{{- define "aedis.kedaBusinessHours" -}}
{{- if and (eq .Values.type "worker") .Values.autoscaling.keda.businessHours.enabled -}}true{{- end -}}
{{- end -}}

{{- define "aedis.kedaMinReplicas" -}}
{{- if include "aedis.kedaBusinessHours" . -}}0
{{- else -}}{{ max (int .Values.autoscaling.keda.minReplicas) (include "aedis.replicaFloor" . | int) }}{{- end -}}
{{- end -}}

{{/* Mínimo efetivo de réplicas (decide o PDB). */}}
{{- define "aedis.minReplicas" -}}
{{- if .Values.autoscaling.keda.enabled -}}{{ include "aedis.kedaMinReplicas" . }}
{{- else if .Values.autoscaling.hpa.enabled -}}{{ include "aedis.hpaMinReplicas" . }}
{{- else -}}{{ include "aedis.replicas" . }}{{- end -}}
{{- end -}}

{{- define "aedis.nodeSelector" -}}
{{- $ns := deepCopy (.Values.nodeSelector | default dict) -}}
{{- with .Values.architecture -}}{{- $_ := set $ns "kubernetes.io/arch" . -}}{{- end -}}
{{- with $ns -}}{{ toYaml . }}{{- end -}}
{{- end -}}

{{- define "aedis.volumeMounts" -}}
- name: tmp
  mountPath: /tmp
{{- range $i, $w := .Values.security.writablePaths }}
{{- if eq (trimSuffix "/" $w.path) "/tmp" }}{{ fail "security.writablePaths: /tmp já é montado (use security.tmpSizeLimit)" }}{{ end }}
- name: writable-{{ $i }}
  mountPath: {{ required "security.writablePaths[].path é obrigatório" $w.path }}
{{- end }}
{{- end -}}

{{- define "aedis.volumes" -}}
- name: tmp
  emptyDir:
  {{- with .Values.security.tmpSizeLimit }}
    sizeLimit: {{ . }}
  {{- else }} {}
  {{- end }}
{{- range $i, $w := .Values.security.writablePaths }}
- name: writable-{{ $i }}
  emptyDir:
  {{- with $w.sizeLimit }}
    sizeLimit: {{ . }}
  {{- else }} {}
  {{- end }}
{{- end }}
{{- end -}}

{{- define "aedis.topologySpread" -}}
{{- $t := .Values.availability.topologySpread -}}
{{- if $t.enabled -}}
{{- if $t.constraints -}}
{{ toYaml $t.constraints }}
{{- else -}}
{{- range $t.topologyKeys }}
- maxSkew: {{ $t.maxSkew }}
  topologyKey: {{ . }}
  whenUnsatisfiable: {{ $t.whenUnsatisfiable }}
  labelSelector:
    matchLabels:
      {{- include "aedis.selectorLabels" $ | nindent 6 }}
{{- end }}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Templates de análise do canário: explícitos ou default por type. */}}
{{- define "aedis.analysisTemplates" -}}
{{- if .Values.rollout.analysis.templates -}}
{{ toYaml .Values.rollout.analysis.templates }}
{{- else if include "aedis.isServed" . -}}
- aedis-http-success-rate
- aedis-http-p95-latency
- aedis-pod-restarts
{{- else -}}
- aedis-pod-restarts
{{- end -}}
{{- end -}}

{{/*
Args passados aos ClusterAnalysisTemplates. O conjunto é por provider: cada template de
charts/aedis-cluster/analysis/<provider>/ declara exatamente estes args.
*/}}
{{- define "aedis.analysisArgs" -}}
- name: namespace
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: rollout
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
- name: canary-hash
  valueFrom:
    podTemplateHashValue: Latest
{{- if has .Values.rollout.analysis.provider (list "datadog" "newrelic" "web") }}
- name: service
  value: {{ include "aedis.name" . | quote }}
- name: env
  value: {{ include "aedis.vendor.envName" . | quote }}
- name: version
  value: {{ include "aedis.version" . | quote }}
{{- end }}
{{- with .Values.rollout.analysis.args }}
{{ toYaml . }}
{{- end }}
{{- end -}}

{{/* Pod template compartilhado por Rollout e Deployment. */}}
{{- define "aedis.podSpec" -}}
{{- $port := .Values.container.port -}}
serviceAccountName: {{ include "aedis.serviceAccountName" . }}
automountServiceAccountToken: {{ .Values.identity.automountToken }}
terminationGracePeriodSeconds: {{ .Values.container.terminationGracePeriodSeconds }}
securityContext:
  {{- include "aedis.podSecurityContext" . | nindent 2 }}
{{- with .Values.image.pullSecrets }}
imagePullSecrets:
  {{- range . }}
  - name: {{ . }}
  {{- end }}
{{- end }}
{{- with (include "aedis.topologySpread" . | trim) }}
topologySpreadConstraints:
  {{- . | nindent 2 }}
{{- end }}
{{- with (include "aedis.nodeSelector" . | trim) }}
nodeSelector:
  {{- . | nindent 2 }}
{{- end }}
{{- with .Values.tolerations }}
tolerations:
  {{- toYaml . | nindent 2 }}
{{- end }}
{{- with .Values.affinity }}
affinity:
  {{- toYaml . | nindent 2 }}
{{- end }}
containers:
  - name: {{ include "aedis.containerName" . }}
    image: {{ include "aedis.image" . }}
    imagePullPolicy: {{ .Values.image.pullPolicy }}
    {{- with .Values.container.command }}
    command:
      {{- toYaml . | nindent 6 }}
    {{- end }}
    {{- with .Values.container.args }}
    args:
      {{- toYaml . | nindent 6 }}
    {{- end }}
    ports:
      - containerPort: {{ $port }}
        name: {{ include "aedis.portName" . }}
        protocol: TCP
    securityContext:
      {{- include "aedis.containerSecurityContext" . | nindent 6 }}
    env:
      {{- include "aedis.env" . | nindent 6 }}
    {{- if or .Values.secrets.keys .Values.secrets.dataFrom }}
    envFrom:
      - secretRef:
          name: {{ include "aedis.name" . }}-secret
    {{- end }}
    resources:
      {{- toYaml .Values.resources | nindent 6 }}
    {{- if .Values.probes.enabled }}
    livenessProbe:
      {{- include "aedis.probe" (dict "root" . "p" .Values.probes.liveness "port" $port) | nindent 6 }}
    readinessProbe:
      {{- include "aedis.probe" (dict "root" . "p" .Values.probes.readiness "port" $port) | nindent 6 }}
    startupProbe:
      {{- include "aedis.probe" (dict "root" . "p" .Values.probes.startup "port" $port "startup" true) | nindent 6 }}
    {{- end }}
    {{- if gt (int .Values.container.preStopSeconds) 0 }}
    lifecycle:
      preStop:
        sleep:
          seconds: {{ .Values.container.preStopSeconds }}
    {{- end }}
    volumeMounts:
      {{- include "aedis.volumeMounts" . | nindent 6 }}
volumes:
  {{- include "aedis.volumes" . | nindent 2 }}
{{- end -}}
