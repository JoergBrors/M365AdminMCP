import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

import pytest  # noqa: E402

from tests.harness import Backend, Gateway, MockEntra  # noqa: E402


@pytest.fixture(scope="session")
def idp():
    return MockEntra()


@pytest.fixture(scope="session")
def backend(idp):
    return Backend(idp)


@pytest.fixture(scope="session")
def gw(idp, backend):
    return Gateway(idp, backend)
