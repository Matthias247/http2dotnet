# CliClient

This example implements a very basic commandline client on top of the HTTP/2
library. It supports fetching making HTTP/2 requests in nghttp style with
various commandline arguments. It also supports a very basic benchmark mode.

The client supports HTTP/2 connections with prior knowledge as well as HTTP/2
upgrade scenarios.

For further information start the client without arguments or with `--help` to
get detailed information.