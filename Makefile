.PHONY: db-up test test-one test-db-reset test-db-template test-db-sweep migrate-dev accept sdk-reference

sdk-reference:
	@tools/provision-sdk-reference.sh

db-up:
	docker compose up -d --wait postgres
	@tools/test-db.sh sweep

test: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build

test-one: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build --filter "$(T)"

test-db-reset: db-up
	@tools/test-db.sh reset "$${MEMSRV_TEST_DATABASE:-memory_test}"

test-db-template: db-up
	@tools/test-db.sh template

test-db-sweep: db-up
	@tools/test-db.sh sweep

migrate-dev:
	docker compose run --rm migrate

accept:
	@echo "make accept: acceptance script is Session 2 scope (docs/memory-server-phase1-spec.md §12); not implemented yet."
	@exit 1
