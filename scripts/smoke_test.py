import os
import subprocess
import sys


def main() -> int:
    env = os.environ.copy()
    env["MEMORY_REQUIRE_DB"] = "1"
    result = subprocess.run(
        [sys.executable, "-m", "pytest", "-m", "integration", "tests/integration"],
        env=env,
    )
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
