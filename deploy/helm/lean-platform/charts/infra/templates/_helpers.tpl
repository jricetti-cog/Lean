{{- define "infra.labels" -}}
app.kubernetes.io/part-of: lean-platform
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}
{{- define "infra.storageClass" -}}
{{- with .Values.storageClass }}
      storageClassName: {{ . }}
{{- end }}
{{- end }}
