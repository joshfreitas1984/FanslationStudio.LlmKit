# Preset glossary audit (2026-10-09)

Corpus audit of the 373 shipped preset entries (`BaseFiles/ChineseGlossary/`) against the translated
splits of DragonHierOverLlm (95k), LegendOfMortalOverLlm (81k; it was 152k before `StringTable` was removed from
its `Converted/` on 2026-10-10) and WanXiangOverLlm (75k). Part of
[`plans/qc-goldset-regression-migration.md`](../plans/qc-goldset-regression-migration.md) workstream E.
Regenerate the data with the manual test "3. Scan: preset glossary corpus audit" in
`FanslationStudio.LlmKit.Assessments` (report: `Files/TestResults/Scans/preset-audit.md`, gitignored).

Method and limits: an entry "matches" a split when it survives the production selection
(`GlossaryLine.SelectFor`: file scoping, longer-match shadowing); it is "used" when the translation
contains the result or an allowed alternative (case-insensitive). A game's own glossary can override a
preset result, so the hit rate measures what the games actually shipped, not preset compliance alone.
Only three games are registered, so "dead" means "unused by these three", not unused everywhere.

Status: **settled**. Every open item from the first pass was decided on 2026-10-09 and applied; the second
review pass is recorded below. Re-run the scan after adding a game or changing preset entries.

## Applied

| Entry | Finding | Change |
| --- | --- | --- |
| 子时 … 亥时 (12 hour entries, `Time`) | Substring matches inside ordinary words (弟子时常 "disciples often", 元阳子时); 0 of 9 / 0 of 32 hits for 子时; games write "midnight", "hour of the Rat" or "noon" | Removed all twelve |
| 月 `Time` | Single character; matches inside 月下, 花月楼, 夜月; hit rate 44% | Removed (年, 日, 秒 stay: 60–100% hits) |
| 命中 `CommonStats` | The verb "to hit" and 命中注定 ("destined"), not the stat; 命中率 is the stat and stays | Removed |
| 太阳 `CommonStats` | Ordinarily "the sun" (太阳打西边出来); "Great Yang" is the meridian reading | Result `Sun`, alternatives `Great Yang`, `Taiyang` |
| 侠客 `Titles` | LegendOfMortal and WanXiang write "Wanderer" | Added alternative `Wanderer` (result unchanged) |
| 三七 `ItemsAndMinerals` | Injected inside 三七二十一 ("regardless of the consequences") and 三七年 | Added idiom 三七二十一; the shadowing rule now covers it. 三七年 still matches |

Decided by the maintainer after review:

- **筋骨** dropped from `CommonStats` (games write "muscles and bones").
- **真人 is Daoist.** The preset already said so; the three games still carried the old rendering. In each
  game's `Converted/`, "Immortal(s)" became "Daoist(s)" on every split whose source contains 真人, across
  `translated`, `qcReviewedText` and `qcTranslated` together so QC freshness is unchanged: DragonHierOverLlm
  153 splits, LegendOfMortalOverLlm 15, WanXiangOverLlm 109. Splits that also contain 仙 or the idiom
  真人不露相 ("real immortals" or "hidden master") were left alone (4, 7 and 4), as were compound-field
  cells whose other fragments mention 仙; a few lines still read "Immortal" for those reasons.
- **Proper nouns moved to their game.** 錦香, 飛石幫, 雪山派, 泥教, 全真, 西夏, 吐蕃, 段家 were already in
  LegendOfMortal's `GameSpecificGlossary.yaml`, so the preset copies were only dropped. 恶人谷, 酒坊, 盐场
  were added to WanXiang's `Glossary.yaml` and dropped from the preset (`Sects`, `Places`).

Second review pass (maintainer decisions, same day):

- **Single-game proper nouns.** Dropped from the preset because LegendOfMortal already had them (same
  result): the six path names 人間道, 修羅道, 地獄道, 畜牲道, 畜生道, 餓鬼道, 六道法王, 奪魄, and the curse
  phrases 戰他娘親, 操你媽 (LegendOfMortal's own entries differ in result, so they are game terms). Moved into the owning game's glossary:
  DragonHierOverLlm 河图, 内门弟子, 外门弟子, 正式弟子, 杜康酒, 竹叶青, 黑金, 黑铁, 镔铁, 铁矿石;
  WanXiangOverLlm 乌金, 龙珠, 江湖酒客, 诛心, 二庄主, 三庄主, 二寨主; LegendOfMortalOverLlm 三俠, 二俠.
- **马帮 "Horse Gang" stays in the preset** (maintainer's call; DragonHier 1/1, LegendOfMortal keeps its own
  identical entry), so it was not moved to DragonHierOverLlm.
- **禁军 stays "Imperial Guard".** The preset already said so and DragonHier uses it (36/36); LegendOfMortal's
  override "Forbidden Army" was removed so it inherits the preset. Its Converted data holds no "Forbidden
  Army" lines, so nothing needed swapping. Note LegendOfMortal also renders 锦衣卫 as "Imperial Guard".
- **Idioms.** 以毒攻毒, 金盆洗手 and 调虎离山 kept with the alternatives `fighting poison with poison`,
  `hands in a golden basin` and `tiger away from the mountain` (hit rates 12/35, 0/40, 6/42 in
  LegendOfMortal became 30/35, 32/40, 27/42). 冰清玉洁, 高手如云 and 冰封 removed: descriptive, and the
  model paraphrases them better than a fixed result. They could return with alternatives that cover the
  paraphrases.
- **First-read leftovers.** 机关 `direct` corrected to "mechanism" and the `Organization` alternative
  dropped (the games write "Mechanism Box", "Mechanism Chest"). 河出马图 (result belonged to 河图洛书),
  海色宝会 and 空军 removed (dead in all three games). 盐帮 and 化神 kept. 承让了 kept (phrases are allowed in the
  preset when they carry good alternatives): a fixed martial-arts courtesy that all three games render the same way (70 of
  80 matches use the preset result).
- The 72 entries unused by all three games stay as generic wuxia vocabulary; revisit only if a fourth game
  suggests otherwise.

Earlier the same day (static lint): `CommonStats` single characters 阴 阳 刚 柔 毒 removed, bad
alternatives and the 化境 typo fixed, a dead 两 entry and a stray YAML item in `Phonetics` fixed.
