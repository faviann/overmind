from __future__ import annotations

import json
import os
import subprocess
import sys
import uuid

import psycopg
import pytest

from cortex_memory import db


pytestmark = pytest.mark.integration


@pytest.fixture(scope="module", autouse=True)
def require_database():
    try:
        with psycopg.connect(db.database_url(), connect_timeout=1):
            pass
    except psycopg.OperationalError as exc:
        if os.environ.get("MEMORY_REQUIRE_DB") == "1":
            pytest.fail(f"Postgres is required but unavailable: {exc}")
        pytest.skip(f"Postgres unavailable: {exc}")


def run_memory(*args: str) -> str:
    command = [sys.executable, "-m", "cortex_memory", *args]
    result = subprocess.run(
        command,
        check=False,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=os.environ.copy(),
    )
    if result.returncode != 0:
        raise AssertionError(
            f"command failed: {' '.join(command)}\n"
            f"stdout:\n{result.stdout}\n"
            f"stderr:\n{result.stderr}"
        )
    return result.stdout.strip()


def parse_json(output: str):
    return json.loads(output)


def test_cli_memory_loop_enforces_approval_and_namespace_isolation():
    namespace = f"repo/memorySubsystem/integration/{uuid.uuid4()}"

    run_memory("init", "--json")

    pending = parse_json(
        run_memory(
            "propose",
            "--namespace",
            namespace,
            "--type",
            "decision",
            "--content",
            "V0a integration memory uses plain text search before embeddings.",
            "--json",
        )
    )
    assert pending["status"] == "pending"

    pending_search = parse_json(
        run_memory(
            "search",
            "--namespace",
            namespace,
            "--query",
            "embeddings",
            "--json",
        )
    )
    assert pending_search == []

    approved = parse_json(run_memory("proposals", "approve", pending["id"], "--json"))
    assert approved["proposal_id"] == pending["id"]
    assert approved["id"] != pending["id"]

    approved_search = parse_json(
        run_memory(
            "search",
            "--namespace",
            namespace,
            "--query",
            "embeddings",
            "--json",
        )
    )
    assert len(approved_search) == 1
    assert approved_search[0]["content"] == pending["content"]

    other_namespace_search = parse_json(
        run_memory(
            "search",
            "--namespace",
            f"{namespace}/other",
            "--query",
            "embeddings",
            "--json",
        )
    )
    assert other_namespace_search == []

    rejected = parse_json(
        run_memory(
            "propose",
            "--namespace",
            namespace,
            "--type",
            "decision",
            "--content",
            "This rejected integration memory must not be searchable.",
            "--json",
        )
    )
    rejected_result = parse_json(run_memory("proposals", "reject", rejected["id"], "--json"))
    assert rejected_result["status"] == "rejected"

    rejected_search = parse_json(
        run_memory(
            "search",
            "--namespace",
            namespace,
            "--query",
            "rejected integration memory",
            "--json",
        )
    )
    assert rejected_search == []

    all_proposals = parse_json(
        run_memory("proposals", "list", "--namespace", namespace, "--status", "all", "--json")
    )
    assert {proposal["status"] for proposal in all_proposals} == {"approved", "rejected"}
