#!/bin/sh
# This host replaces every data service with in-memory fixtures. Never runs Program.cs from the app.
set -eu
PACKTRACKER_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export PACKTRACKER_ROOT
fixture_dir=$(mktemp -d /tmp/packtracker-dashboard.XXXXXX)
cp "$PACKTRACKER_ROOT/scripts/dashboard-verification/Program.cs.txt" "$fixture_dir/Program.cs"
cat > "$fixture_dir/Fixtures.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><ProjectReference Include="$PACKTRACKER_ROOT/PetPotty.csproj" /></ItemGroup></Project>
PROJECT
dotnet run --project "$fixture_dir/Fixtures.csproj" --urls http://127.0.0.1:5088
