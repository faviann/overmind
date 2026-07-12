.PHONY: db-up test test-one test-db-reset migrate-dev accept sdk-reference

sdk-reference:
	@tools/provision-sdk-reference.sh

db-up:
	docker compose up -d --wait postgres

test: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build

test-one: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build --filter "$(T)"

test-db-reset: db-up
	docker compose exec postgres psql -U overmind -d postgres \
		-c "DROP DATABASE IF EXISTS memory_test WITH (FORCE);" \
		-c "CREATE DATABASE memory_test;"
	MEMSRV_ADMIN_CONNECTION_STRING="postgres://overmind:overmind_dev@127.0.0.1:55432/memory_test" \
		dotnet run --project src/MemCtl -- migrate

migrate-dev:
	docker compose run --rm migrate

accept:
	@echo "make accept: acceptance script is Session 2 scope (docs/memory-server-phase1-spec.md §12); not implemented yet."
	@exit 1
