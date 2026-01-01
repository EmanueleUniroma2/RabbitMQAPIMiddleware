#!/bin/bash
set -e
export BINARY_NAME="RabbitMQAPIMiddleware"
export DOTNET_VERSION="net8.0"
export SKIP_GIT_SYNC=false

for arg in "$@"; do
  if [ "$arg" == "--no-commit" ]; then
    export SKIP_GIT_SYNC=true
  fi
done

curl -H "Cache-Control: no-cache" -s https://raw.githubusercontent.com/EmanueleUniroma2/MyDeployToolkit/refs/heads/main/installer.sh | bash
