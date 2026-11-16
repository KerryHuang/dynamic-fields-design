#!/usr/bin/env bash
# 把一個測試專案依「測試類別」切成 N 份，只跑第 i 份：
#   bash scripts/ci/shard-tests.sh <專案名，如 ApplicationTests> <第幾份，從 1 起> <共幾份>
# 切法從 dotnet test --list-tests 的實際清單算出（不寫死類別名），所以新增類別會自動歸某一份、不會漏測：
#   以類別為單位，權重取 shard-weights.tsv 的實測秒數（沒記的類別按測試數估），由大到小貪婪分配給目前最輕的一份（結果固定）。
#   Demo 類別各自獨立分配：DemoGeneratedFixture 在每份各建一次，換得 Demo 測試（同 collection、只能序列跑）能分散。
#   某份又明顯變慢時，從該份輸出的「最耗時的類別」更新 shard-weights.tsv 即可。
# 驗證：本份實際執行的 Total 必須等於清單中分給本份的測試數；各份的 expected 加總 = 清單總數（見輸出的 sum 行）。
# 本機只想看分配、不跑測試：DRY_RUN=1。
set -euo pipefail
proj="${1:?專案名}"; idx="${2:?第幾份}"; total="${3:?共幾份}"
here="$(cd "$(dirname "$0")" && pwd)"
cd "$(git rev-parse --show-toplevel)/backend"
dir="tests/DynamicModules.$proj"
out="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/shard-$proj-$idx"; mkdir -p "$out"

dotnet build "$dir" --nologo -v q
dotnet test "$dir" --no-build --list-tests 2>/dev/null | sed -n '/^The following Tests are available:/,$p' | tail -n +2 \
  | sed 's/^ *//' | grep -v '^$' > "$out/tests.txt"

# class<TAB>測試數<TAB>shard，由 awk 一次算完
# 權重 = shard-weights.tsv 記的實測秒數（量過的重類別），其餘類別以每筆 0.5 秒估算
awk -v n="$total" -v proj="$proj" -v wf="$here/shard-weights.tsv" '
  BEGIN { while ((getline l < wf) > 0) { split(l, a, "\t"); sec[a[1]] = a[2] } }
  { name=$0; sub(/\(.*/,"",name); c=name; sub(/\.[^.]*$/,"",c); cnt[c]++; all++ }
  END {
    k=0
    for (c in cnt) {
      key=c; sub(/^DynamicModules\./,"",key)
      w[c] = (key in sec) ? sec[key] : cnt[c]*0.5
      us[++k]=c
    }
    # 權重大到小、同權重依名稱
    for (i=2;i<=k;i++){v=us[i]; j=i-1; while(j>0 && (w[us[j]]<w[v] || (w[us[j]]==w[v] && us[j]>v))){us[j+1]=us[j]; j--} us[j+1]=v}
    for (s=1;s<=n;s++) load[s]=0
    for (i=1;i<=k;i++){ b=1; for(s=2;s<=n;s++) if(load[s]<load[b]) b=s; load[b]+=w[us[i]]; sh[us[i]]=b; ld[b]+=cnt[us[i]] }
    for (c in cnt) printf "%s\t%d\t%d\n", c, cnt[c], sh[c]
    for (s=1;s<=n;s++) printf "第 %d 份預估 %d 秒、%d 筆\n", s, load[s], ld[s] > "/dev/stderr"
  }' "$out/tests.txt" 2> "$out/load.txt" | sort > "$out/map.tsv"

all=$(wc -l < "$out/tests.txt" | tr -d ' ')
sum=$(awk -F'\t' '{s+=$2} END{print s+0}' "$out/map.tsv")
echo "清單總數=${all}，各類別加總=${sum}"; [ "$all" = "$sum" ] || { echo "分配加總與清單總數不符"; exit 1; }
cat "$out/load.txt"
expected=$(awk -F'\t' -v i="$idx" '$3==i{s+=$2} END{print s+0}' "$out/map.tsv")
filter=$(awk -F'\t' -v i="$idx" '$3==i{printf "%sFullyQualifiedName~%s.", (f++?"|":""), $1}' "$out/map.tsv")
[ -n "$filter" ] || { echo "第 ${idx} 份沒有任何測試"; exit 1; }
echo "本份 ${proj} ${idx}/${total}：類別 $(awk -F'\t' -v i="$idx" '$3==i' "$out/map.tsv" | wc -l | tr -d ' ') 個，預期 ${expected} 筆"
[ "${DRY_RUN:-}" = 1 ] && exit 0

set +e
dotnet test "$dir" --no-build --filter "$filter" --logger "console;verbosity=minimal" \
  --logger "trx;LogFileName=$out/result.trx" 2>&1 | tee "$out/run.log"
code=${PIPESTATUS[0]}
set -e

# 各類別耗時前 15 名，供下次調整切法
if [ -f "$out/result.trx" ]; then
  { echo "### $proj $idx/$total 各類別耗時前 40 名（秒，更新 shard-weights.tsv 用）"
    grep -o 'testName="[^"]*" computerName="[^"]*" duration="[^"]*"' "$out/result.trx" \
      | sed -E 's/testName="([^"(]*)[^"]*" computerName="[^"]*" duration="([0-9]+):([0-9]+):([0-9.]+)"/\1 \2 \3 \4/' \
      | awk '{c=$1; sub(/\.[^.]*$/,"",c); t[c]+=$2*3600+$3*60+$4} END{for(k in t) printf "%8.1f %s\n", t[k], k}' | sort -nr | head -40
  } | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
fi

got=$(grep -oE 'Total: *[0-9]+' "$out/run.log" | tail -1 | grep -oE '[0-9]+' || echo 0)
echo "實際執行 Total=${got}，預期=${expected}"
[ "$got" = "$expected" ] || { echo "實際執行數與預期不符，可能漏測或重複"; exit 1; }
exit "$code"
