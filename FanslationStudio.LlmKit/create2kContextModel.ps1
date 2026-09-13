@"
FROM "hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL"

PARAMETER num_ctx 2048
"@ | Set-Content -Encoding UTF8 "$env:TEMP\qwen38-8b-2048.Modelfile"

ollama create qwen38-27B-2048-unsloth -f "$env:TEMP\qwen38-8b-2048.Modelfile"