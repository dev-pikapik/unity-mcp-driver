#!/bin/sh
# ./build.sh                       -> udriver-il2cpp.dylib (macOS arm64)
# ./build.sh tools ToolManifest.cs -> regenerate tools.json from the managed tool list
set -e
cd "$(dirname "$0")"

if [ "$1" = tools ]; then
    t=$(mktemp -d)
    cp "$2" "$t/ToolManifest.cs"
    cat > "$t/gen.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><RollForward>LatestMajor</RollForward></PropertyGroup></Project>
EOF
    cat > "$t/Gen.cs" <<'EOF'
using System.Linq;
using System.Text.Json.Nodes;
var arr = new JsonArray();
foreach (var d in UDriver.ToolManifest.Tools)
{
    var props = new JsonObject(); var req = new JsonArray();
    foreach (var p in d.Params)
    {
        var o = new JsonObject { ["description"] = p.Desc };
        if (p.Type != "any") o["type"] = p.Type;
        if (p.Type == "array") o["items"] = new JsonObject();
        props[p.Name] = o;
        if (p.Required) req.Add(p.Name);
    }
    arr.Add(new JsonObject { ["name"] = d.Name, ["description"] = d.Desc,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req } });
}
System.Console.Write(arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
EOF
    dotnet run --project "$t/gen.csproj" > tools.json
    rm -rf "$t"
    exit
fi

case "$(uname -s)" in
Darwin) clang -O2 -arch arm64 -dynamiclib -std=c2x -Wall -Wno-unused-function -o udriver-il2cpp.dylib udriver.c cJSON.c \
            -framework ApplicationServices -framework AppKit -lobjc -lpthread ;;
Linux)  clang -O2 -shared -fPIC -std=c2x -Wall -o libudriver-il2cpp.so udriver.c cJSON.c -lpthread -ldl -lm ;;
*)      clang --target=x86_64-w64-mingw32 -O2 -shared -std=c2x -o udriver-il2cpp.dll udriver.c cJSON.c -lws2_32 ;;
esac
