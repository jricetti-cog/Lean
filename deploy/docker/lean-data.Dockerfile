# lean-data seed image (plan step s6.3): the repository's Data/ folder plus the seed script, so a Kubernetes Job can
# fill the lean-data PersistentVolumeClaim the way deploy/compose/lean-data/seed-lean-data.sh fills the Compose volume.
# Built with the Data/ folder as context and the seed scripts as the named `seed` context (Services/docker-bake.hcl):
#   docker buildx bake -f Services/docker-bake.hcl lean-data
FROM alpine:3.22.8
COPY --from=seed seed-lean-data.sh /seed/seed-lean-data.sh
COPY . /source/
ENTRYPOINT ["/bin/sh", "/seed/seed-lean-data.sh"]
