#!/bin/sh
# Runs once, on the first start of an empty postgres-data volume (docker-entrypoint-initdb.d).
# ADR 0003: one database and one login role per owning service; no service can connect to another's
# database. Schemas are not created here: each service applies its own migrations on start.
set -eu

create_service_database() {
    db="$1"
    password="$2"
    echo "creating database ${db} owned by role ${db}"
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
        -v db="$db" -v password="$password" <<'SQL'
CREATE ROLE :"db" LOGIN PASSWORD :'password';
CREATE DATABASE :"db" OWNER :"db";
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO :"db";
SQL
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" -v db="$db" <<'SQL'
ALTER SCHEMA public OWNER TO :"db";
REVOKE ALL ON SCHEMA public FROM PUBLIC;
SQL
}

create_service_database jobs "$LEAN_DB_JOBS_PASSWORD"
create_service_database results "$LEAN_DB_RESULTS_PASSWORD"
create_service_database gateway "$LEAN_DB_GATEWAY_PASSWORD"
create_service_database marketdata "$LEAN_DB_MARKETDATA_PASSWORD"
