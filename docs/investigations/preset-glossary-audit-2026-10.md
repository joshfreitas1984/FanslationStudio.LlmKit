# Preset glossary audit (2026-10-09)

Corpus audit of the 373 shipped preset entries (`BaseFiles/ChineseGlossary/`) against the translated
splits of DragonHierOverLlm (95k), LegendOfMortalOverLlm (152k) and WanXiangOverLlm (75k). Part of
[`plans/qc-goldset-regression-migration.md`](../plans/qc-goldset-regression-migration.md) workstream E.
Regenerate the data with the manual test "3. Scan: preset glossary corpus audit" in
`FanslationStudio.LlmKit.Assessments` (report: `Files/TestResults/Scans/preset-audit.md`, gitignored).

Method and limits: an entry "matches" a split when it survives the production selection
(`GlossaryLine.SelectFor`: file scoping, longer-match shadowing); it is "used" when the translation
contains the result or an allowed alternative (case-insensitive). A game's own glossary can override a
preset result, so the hit rate measures what the games actually shipped, not preset compliance alone.
Only three games are registered, so "dead" means "unused by these three", not unused everywhere.

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

Earlier the same day (static lint): `CommonStats` single characters 阴 阳 刚 柔 毒 removed, bad
alternatives and the 化境 typo fixed, a dead 两 entry and a stray YAML item in `Phonetics` fixed.

## Open: needs a human decision

- **Remaining single-game proper nouns** (about 44 of the 68): 三俠/二俠, 六道 path names, 奪魄, 內/外門 and
  other sect ranks, 禁军, 杜康酒, and the Dragon Heir materials. Same rubric as above; move them the same way
  when someone wants them gone from the preset.
- **Contested idioms** (以毒攻毒, 冰清玉洁, 调虎离山, 金盆洗手, 高手如云, 冰封): the model paraphrases
  ("fighting poison with poison" misses "Fight poison with poison"). Candidates for alternatives or
  removal; the rubric says phrases do not belong in the preset.
- **72 entries unused by all three games.** Keep as generic wuxia vocabulary unless a fourth game
  suggests otherwise; do not delete on this evidence alone.
- **Still from the first read, not yet re-checked against the corpus:** 承让了 (a phrase), 河图 versus
  河出马图, 机关 `direct: Organization`, 马帮 / 盐帮 / 海色宝会 / 空军 (game-specific), 化神.
