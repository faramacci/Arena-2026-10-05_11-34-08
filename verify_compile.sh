#!/usr/bin/env bash
# Compiles Assets/Scripts/**.cs against Unity's own Roslyn + the exact reference
# set Unity put in Assembly-CSharp.csproj.
# Uses python for fast reference resolution and robust Windows quoted path handling in csc.rsp.

set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 2

python -c "
import os, sys, re, glob, subprocess

csp_file = 'Assembly-CSharp.csproj'
if not os.path.exists(csp_file):
    print('FATAL: Assembly-CSharp.csproj not found')
    sys.exit(2)

with open(csp_file, 'r', encoding='utf-8') as f:
    content = f.read()

refs = re.findall(r'<HintPath>(.*?)</HintPath>', content)
ref_args = ['\"-r:' + os.path.normpath(r) + '\"' for r in refs if os.path.exists(r)]

assembled = 0
for dll in glob.glob('Library/ScriptAssemblies/*.dll'):
    base = os.path.basename(dll)
    if base in ['Assembly-CSharp.dll', 'Assembly-CSharp-Editor.dll', 'Assembly-CSharp-firstpass.dll']:
        continue
    ref_args.append('\"-r:' + os.path.normpath(os.path.abspath(dll)) + '\"')
    assembled += 1

defines_match = re.search(r'<DefineConstants>(.*?)</DefineConstants>', content)
defines = ['-define:' + d for d in defines_match.group(1).split(';') if d] if defines_match else []

lang_match = re.search(r'<LangVersion>(.*?)</LangVersion>', content)
lang = lang_match.group(1) if lang_match else 'latest'

sources = []
for root, dirs, files in os.walk('Assets'):
    for file in files:
        if file.endswith('.cs'):
            sources.append('\"' + os.path.normpath(os.path.join(root, file)) + '\"')

sources.sort()

out_dir = 'Temp/verify-compile'
os.makedirs(out_dir, exist_ok=True)
rsp_path = os.path.join(out_dir, 'csc.rsp')
out_dll = os.path.join(out_dir, 'Assembly-CSharp.dll')

with open(rsp_path, 'w', encoding='utf-8') as f:
    f.write('-nologo\n')
    f.write('-target:library\n')
    f.write(f'-langversion:{lang}\n')
    f.write('-nostdlib+\n')
    f.write(f'\"-out:{out_dll}\"\n')
    for d in defines:
        f.write(f'{d}\n')
    for r in ref_args:
        f.write(f'{r}\n')
    for s in sources:
        f.write(f'{s}\n')

dotnet = 'O:/Unity/Editor/6000.6.4f1/Editor/Data/DotNetSdk/dotnet.exe'
csc = 'O:/Unity/Editor/6000.6.4f1/Editor/Data/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll'

res = subprocess.run([dotnet, 'exec', csc, f'@{rsp_path}'], capture_output=True, text=True)

errors_txt = os.path.join(out_dir, 'errors.txt')
with open(errors_txt, 'w', encoding='utf-8') as f:
    f.write(res.stdout + '\n' + res.stderr)

cs_errors = [line for line in (res.stdout + '\n' + res.stderr).splitlines() if 'error CS' in line]

if cs_errors:
    print(f'---- COMPILE ERRORS: {len(cs_errors)} ----')
    for e in cs_errors[:20]:
        print(e)
    sys.exit(1)

if res.returncode != 0:
    print('---- NON-CS FAILURE ----')
    print(res.stderr)
    sys.exit(1)

print(f'OK: compiled clean ({len(sources)} sources, {len(ref_args)} refs) -> {out_dll}')
sys.exit(0)
"
