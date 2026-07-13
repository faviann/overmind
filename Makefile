.PHONY: db-up test test-one benchmark-test test-db-reset test-db-template test-db-sweep migrate-dev accept sdk-reference smoke-image

sdk-reference:
	@tools/provision-sdk-reference.sh

db-up:
	docker compose up -d --wait postgres
	@tools/test-db.sh sweep

test: db-up
	dotnet build memsrv.sln
	@tools/run-test-suite.sh

test-one: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build --filter "$(T)"

benchmark-test:
	@tools/benchmark-test.sh

test-db-reset: db-up
	dotnet build memsrv.sln
	@tools/test-db.sh reset "$${MEMSRV_TEST_DATABASE:-memory_test}"

test-db-template: db-up
	dotnet build memsrv.sln
	@tools/test-db.sh template

test-db-sweep: db-up
	@tools/test-db.sh sweep

migrate-dev:
	docker compose run --rm migrate

accept: db-up
	dotnet build memsrv.sln
	dotnet test tests/MemSrv.Tests --no-build --filter "FullyQualifiedName~MemSrv.Tests.AcceptanceTests"

smoke-image:
	@test -n "$(IMAGE)" || { printf 'usage: make smoke-image IMAGE=<image> [PULL=1]\n' >&2; exit 2; }
	@if [ "$(PULL)" = 1 ]; then \
		tools/smoke-release-image.sh --pull "$(IMAGE)"; \
	else \
		tools/smoke-release-image.sh "$(IMAGE)"; \
	fi
