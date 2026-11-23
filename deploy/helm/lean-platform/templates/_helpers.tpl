{{- define "lean.labels" -}}
app.kubernetes.io/part-of: lean-platform
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}

{{- define "lean.secretName" -}}
{{- .Values.existingSecret | default "lean-platform-env" -}}
{{- end }}

{{/* image: (dict "root" $ "name" "results") */}}
{{- define "lean.image" -}}
{{- printf "%s/%s:%s" .root.Values.image.registry .name (.root.Values.image.tag | default .root.Chart.AppVersion) -}}
{{- end }}

{{/* Init containers: infra reachable, and (data users) lean-data seeded. (dict "root" $ "data" "ro") */}}
{{- define "lean.waitFor" -}}
- name: wait-for-infra
  image: {{ .root.Values.waitImage }}
  command:
    - sh
    - -c
    - |
      for target in {{ .root.Values.endpoints.rabbitmq.host }}:{{ .root.Values.endpoints.rabbitmq.port }} {{ .root.Values.endpoints.postgres.host }}:{{ .root.Values.endpoints.postgres.port }} {{ regexReplaceAll "^https?://([^/]+).*$" .root.Values.endpoints.s3.serviceUrl "${1}" }}; do
        host=${target%:*}; port=${target##*:}
        until nc -z -w 2 "$host" "$port"; do echo "waiting for $target"; sleep 2; done
      done
  securityContext: { allowPrivilegeEscalation: false }
{{- if and .data .root.Values.data.seed.enabled }}
- name: wait-for-lean-data
  image: {{ .root.Values.waitImage }}
  command: ["sh", "-c", "until [ -f /Lean/Data/.lean-data-seeded ]; do echo 'waiting for the lean-data seed Job'; sleep 3; done"]
  securityContext: { allowPrivilegeEscalation: false }
  volumeMounts: [{ name: lean-data, mountPath: /Lean/Data, readOnly: true }]
{{- end }}
{{- end }}
