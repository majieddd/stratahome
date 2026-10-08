"""Render the checked-in model snapshot. Print fragments; --check validates docs."""
import argparse
import collections
import html
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'docs/data/model-comparison.json'
BEGIN = '<!-- BEGIN MODEL COMPARISON -->'
END = '<!-- END MODEL COMPARISON -->'


def outputs(data):
    rows = data['variants']
    sources = data['sources']
    esc = html.escape

    def cite(number, web=False):
        if number == 'local':
            return '<a href="data/local-swift-iq2-xs.txt">[local]</a>' if web else '[local](docs/data/local-swift-iq2-xs.txt)'
        if number == 'strata-q4':
            number = 13
        url = sources[str(number)]['url']
        return f'<a href="{esc(url, quote=True)}">[{number}]</a>' if web else f'[{number}]({url})'

    def model_link(row, web=False):
        url = sources[str(row['source'])]['url']
        label = row['family']
        return f'<a href="{esc(url, quote=True)}">{esc(label)}</a>' if web else f'[{label}]({url})'

    def speed(row, web=False):
        record = row['decode']
        if not record:
            return 'Not recorded'
        value = str(record['tps']) if 'tps' in record else '–'.join(map(str, record['tps_range']))
        return value + ' ' + cite(record['source'], web)

    def kld(row, web=False):
        record = row['kld']
        if not record:
            return 'Reference (BF16)' if row['tier'] == 'BF16' else 'Not located'
        return f"{record['value']:.6f} " + cite(record['source'], web)

    def method(row):
        record = row['kld']
        if not record:
            return 'BF16 reference' if row['tier'] == 'BF16' else 'No quant-specific evaluation located'
        context = str(record['context']) if record['context'] else 'unspecified'
        return f"{record['corpus']}; context {context}; vs {record['reference']}"

    def quality(row):
        record = row['quality']
        if isinstance(record, dict):
            return '; '.join(f'{k}: {v:.2f}' for k, v in record.items())
        return record

    headers = ['Model / source', 'Quant', 'Files (GB)', 'Decode tok/s', 'Mean KLD ↓', 'KLD protocol / reference', 'Quality evidence', 'Approximate hardware / capacity']

    def cells(row, web=False):
        return [model_link(row, web), row['tier'], f"{row['size_bytes']/1e9:.2f}", speed(row, web), kld(row, web), method(row), quality(row), row['hardware']]

    def md_table(group):
        out = ['| ' + ' | '.join(headers) + ' |', '| ' + ' | '.join(['---'] * len(headers)) + ' |']
        for row in group:
            out.append('| ' + ' | '.join(cells(row)) + ' |')
        return '\n'.join(out)

    def web_table(group, label):
        out = [f'<div class="comparison-scroll" tabindex="0" role="region" aria-label="{esc(label)}"><table class="comparison-table">', f'<caption>{esc(label)}</caption>', '<thead><tr>' + ''.join(f'<th scope="col">{esc(h)}</th>' for h in headers) + '</tr></thead><tbody>']
        for row in group:
            c = cells(row, True)
            # Only these cells contain generated, escaped anchors.
            rendered = [c[i] if i in (0, 3, 4) else esc(c[i]) for i in range(len(c))]
            active = ' class="our-model"' if row['family'] == 'Swift 1.5 GSQ-RCO' and row['tier'] == 'IQ2_XS' else ''
            out.append('<tr' + active + '>' + ''.join('<td>' + x + '</td>' for x in rendered) + '</tr>')
        out.append('</tbody></table></div>')
        return '\n'.join(out)

    groups = [('core', 'The four requested families — BF16 and all GSQ-RCO tiers'), ('swift-standard', 'Standard Swift GGUF — all 27 publisher tiers'), ('normal-community', 'Normal Qwen — all 10 Unsloth quantized tiers'), ('accelerators', 'Swift — three additional accelerator formats')]
    intro = 'Snapshot: October 8, 2026. File sizes are decimal GB, summed from the Hugging Face file inventory; GGUF totals exclude separate vision projectors and MTP drafts. Not recorded / Not located means no matching measurement was found in the reviewed sources. It does not mean zero.'
    caveats = [
        'Our installed model is Swift 1.5 GSQ-RCO IQ2_XS. The saved Strata run reports 126.0 tok/s over 265 generated tokens (thinking included), with 128.1 tok/s during answering. Hardware: RTX 5090 Laptop 24 GB, Core Ultra 9 275HX, 64 GB RAM. This is one short run, not an average or a benchmark sweep; configured maximum context was 262144, not a measured 262K prompt. The installed weights were not hash-matched to today’s Hugging Face revision.',
        'Qwen GSQ-RCO speeds [8] are Strata’s recorded RTX 5070 12 GB / Ryzen 5 7600 / 64 GB RAM results. Short-chat decode uses 4K answers; Q2_0 used engine 0.1.36, the other tiers 0.1.26. Prefill uses a 32K prompt. These are not Swift measurements or promises for a different PC.',
        'KLD is divergence from a reference distribution, not percent accuracy. The GSQ rows use English C4 prose at 512 tokens; standard Swift uses WikiText-2 at 512 tokens; Unsloth’s displayed table does not specify corpus/context. Do not rank these protocols against each other. Qwen and Swift also use different BF16 references, so cross-family KLD does not establish which model is smarter.',
        'Strata capacity estimates [8]: Q2_0 / IQ2_XS normally target 48 GB RAM; IQ3_XXS / IQ3_S target 64 GB, with a supported 12 GB+ GPU and SSD. Original GSQ transformer weights are about 37.6 / 39.2 / 47.0 / 54.8 GB; the ~28.8 GB n-gram lookup can remain mapped on SSD. Swift estimates use corresponding tiers, not a verified fit test of the current release. Do not infer Swift memory residency from shard 1: Swift splits are packaged differently.',
        'For other formats, weight-file size is not a VRAM minimum or a guarantee of fitting. CPU/GPU offload, sparse lookup paging, OS reserve, context/KV cache, vision and MTP change RAM/VRAM use. BF16 checkpoints are about 360 GB including auxiliary weights. Supplemental formats are reference options, not a claim that StrataHome/Strata can load every file without conversion.',
        'Publisher BF16 benchmark scores must not be copied onto the quantized rows. The five model-level comparisons below are UkisAI’s own BF16 evaluation; ISTA’s GSQ task scores come from a separate evaluation. Values above the BF16 reference in ISTA’s tests can reflect variance, not a guaranteed improvement.',
        'Swift’s reduced reasoning length is not a 1.8× tokens-per-second claim. At GPQA xhigh, the publisher reports 55.8% fewer mean thinking tokens and 63.4% fewer median tokens. Accuracy differences vary by task and reasoning setting; see the scores below instead of treating “less than 1% loss” as universal.',
        'Model licences are separate from StrataHome’s MIT licence. Consult each model repository’s current terms; Swift and Qwen have custom licence conditions. Two ordinary HF checkpoint links contain BF16, not GGUF quant menus.',
    ]
    md = ['# Model and quantization reference', '', intro, '', data['scope'], '', '## Models and downloads', '']
    for n, label in [(1, 'Normal Qwen'), (2, 'Swift 1.5 normal'), (3, 'Qwen GSQ-RCO'), (4, 'Swift 1.5 GSQ-RCO — our installed family')]:
        md.append(f'- **{label}:** {cite(n)}')
    for key, label in groups:
        md += ['', '## ' + label, '', md_table([r for r in rows if r['group'] == key])]
    md += ['', '## Recorded speed conditions', '', '| Model / quant | Short decode tok/s | 128K decode tok/s | 32K prefill tok/s | Machine / source |', '|---|---:|---:|---:|---|']
    for row in rows:
        s = row['decode']
        if not s:
            continue
        md.append(f"| {row['family']} / {row['tier']} | {speed(row)} | {s.get('long_context_tps', 'Not recorded')} | {s.get('prefill_tps', 'Not recorded')} | {s['hardware']} {s['notes']} |")
    md += ['', '### Separate llama.cpp measurement (not a gaming-PC Strata result)', '', 'ISTA also reports Q2_0: 93.79 decode / 367.49 prompt tok/s; IQ2_XS: 70.30 decode / 108.19 prompt tok/s across 55 prompts. Its displayed performance section does not identify the hardware, so these are not used as hardware-qualified recommendations.[3]', '', '## BF16 model-level quality — same publisher evaluation', '', '| Benchmark | Normal Qwen BF16 | Swift 1.5 BF16 | Base tokens | Swift tokens |', '|---|---:|---:|---:|---:|']
    for b in data['bf16_benchmarks']:
        md.append('| ' + ' | '.join([b['benchmark'], b['base'], b['swift'], b['base_tokens'], b['swift_tokens']]) + ' |')
    md += ['', 'Source [2]: tokens are mean thinking tokens except Terminal-Bench (total output). These are BF16 results, not scores for GSQ or standard GGUF tiers. Reasoning evaluations used xhigh and disabled MTP. Terminal-Bench used concurrency 8 for Swift and a context-recovery fix, so serving conditions were not fully identical.', '', '## How to interpret the reference', '']
    md += ['- ' + c for c in caveats]
    md += ['', '## Extra KLD measurements', '', 'The [machine-readable snapshot](docs/data/model-comparison.json) includes all GSQ reporting domains, errors/chunk counts, standard Swift 32K KLD and top-token agreement, source revisions, byte totals and shard names. Those are evaluation proxies, not task-accuracy scores. Swift Q2_0 remains experimental; current upstream Swift IQ3_S exists even though the reviewed Strata installer documentation does not list it.', '', '## Sources:', '']
    for n, source in sorted(sources.items(), key=lambda x: int(x[0])):
        md.append(f"[{n}] {source['url']}" + (f" — snapshot revision `{source['revision']}`" if 'revision' in source else ''))
    md += ['', 'Local evidence: [sanitized Strata run and hardware record](docs/data/local-swift-iq2-xs.txt). This page republishes recorded results; it does not claim a new all-model local benchmark.', '']

    web = [BEGIN, '<section class="stratum t-tint" id="models" aria-labelledby="h-models">', '<i class="bg" aria-hidden="true"></i><i class="seam" aria-hidden="true"><b></b><b></b><b></b><b></b></i>', '<div class="wrap">', '<p class="kicker">Models &amp; recorded performance</p>', '<h2 id="h-models">Pick the model, then the quant.</h2>', '<p class="lede">Our installed model: <a href="' + sources['4']['url'] + '"><strong>Swift 1.5 GSQ-RCO IQ2_XS</strong></a>. The desktop app does not bundle or download model weights.</p>', '<p class="comparison-note">' + esc(intro) + '</p>', '<ul class="model-links">']
    for n, label in [(1, 'Normal Qwen'), (2, 'Swift 1.5 normal'), (3, 'Qwen GSQ-RCO'), (4, 'Swift 1.5 GSQ-RCO — our installed family')]:
        web.append('<li>' + esc(label) + ' ' + cite(n, True) + '</li>')
    web += ['</ul>', '<p class="comparison-note">' + esc(data['scope']) + '</p>']
    for key, label in groups:
        table = web_table([r for r in rows if r['group'] == key], label)
        if key == 'core':
            web.append(table)
        else:
            web += ['<details class="comparison-details"><summary>' + esc(label) + '</summary>', table, '</details>']
    web += ['<h3 class="comparison-heading">Speed, hardware and context together</h3>', '<div class="comparison-scroll" tabindex="0" role="region" aria-label="Recorded speed conditions"><table class="comparison-table speed-table"><caption>Recorded speeds — different workloads are not a single leaderboard</caption><thead><tr><th scope="col">Model / quant</th><th scope="col">Short decode tok/s</th><th scope="col">128K decode tok/s</th><th scope="col">32K prefill tok/s</th><th scope="col">Machine / conditions</th></tr></thead><tbody>']
    for row in rows:
        s = row['decode']
        if s:
            web.append('<tr>' + ''.join('<td>' + c + '</td>' for c in [esc(row['family'] + ' / ' + row['tier']), speed(row, True), esc(str(s.get('long_context_tps', 'Not recorded'))), esc(str(s.get('prefill_tps', 'Not recorded'))), esc(s['hardware'] + ' ' + s['notes'])]) + '</tr>')
    web += ['</tbody></table></div>', '<p class="comparison-note">Separate llama.cpp result: Q2_0 93.79 decode / 367.49 prompt tok/s; IQ2_XS 70.30 / 108.19. The displayed ISTA performance section does not specify hardware; do not compare it directly with Strata’s gaming-PC results. ' + cite(3, True) + '</p>', '<h3 class="comparison-heading">Model-level quality: BF16, not quant scores</h3>', '<div class="comparison-scroll" tabindex="0" role="region" aria-label="BF16 quality comparison"><table class="comparison-table quality-table"><caption>UkisAI BF16 comparison — shared publisher evaluation</caption><thead><tr><th scope="col">Benchmark</th><th scope="col">Normal Qwen</th><th scope="col">Swift 1.5</th><th scope="col">Base tokens</th><th scope="col">Swift tokens</th></tr></thead><tbody>']
    for b in data['bf16_benchmarks']:
        web.append('<tr>' + ''.join('<td>' + esc(b[k]) + '</td>' for k in ['benchmark','base','swift','base_tokens','swift_tokens']) + '</tr>')
    web += ['</tbody></table></div>', '<p class="comparison-note">Mean thinking tokens, except Terminal-Bench (total output). Xhigh, MTP disabled for the reasoning evaluation. Terminal-Bench serving conditions differ; Swift used concurrency 8 and a context-recovery fix. Quant-specific task scores are not inferred from this table. ' + cite(2, True) + '</p>', '<details class="comparison-details" open><summary>Read before comparing KLD, speed or hardware</summary><ul class="comparison-caveats">']
    web += ['<li>' + esc(c) + '</li>' for c in caveats]
    web += ['</ul></details>', '<p class="comparison-note"><a href="https://github.com/majieddd/stratahome/blob/main/MODEL_COMPARISON.md">Full GitHub reference and sources</a> · <a href="data/model-comparison.json">Download the 50-row data snapshot</a> · <a href="data/local-swift-iq2-xs.txt">Local speed evidence</a></p>', '</div></section>', END]
    summary = [BEGIN, '## Models, quantizations and recorded speeds', '', '**Our installed model:** [Swift 1.5 GSQ-RCO IQ2_XS](' + sources['4']['url'] + ').', '', intro, '', md_table([r for r in rows if r['group'] == 'core']), '', '**Read the protocols before ranking:** C4 KLD, WikiText KLD, task accuracy and top-token agreement are different measurements. Each family uses its own BF16 reference. The local Swift 126.0 tok/s is one installed-model run, not a full benchmark sweep or a claim for today’s HF release. Hardware estimates need context/cache/OS headroom.', '', '**[Full 50-row comparison](MODEL_COMPARISON.md)** includes all 27 standard Swift GGUF tiers, all 10 Unsloth normal-Qwen quants, AWQ, AutoRound and NVFP4, the recorded test machines/context, model-level BF16 scores, missing-data labels and pinned sources. Also on the [download page](https://majieddd.github.io/stratahome/#models).', END]
    return {'markdown': '\n'.join(md), 'html': '\n'.join(web), 'summary': '\n'.join(summary)}


def validate(data):
    rows = data['variants']
    assert len(rows) == 50
    assert len({(r['model_id'], r['tier']) for r in rows}) == 50
    assert collections.Counter(r['group'] for r in rows) == {'core':10,'swift-standard':27,'normal-community':10,'accelerators':3}
    for row in rows:
        assert row['size_bytes'] == sum(f['size'] for f in row['files'])
        assert row['size_bytes'] > 0
        assert str(row['source']) in data['sources']
        assert row['decode'] is None or (row['decode'].get('tps', 1) > 0)
        if row['kld']:
            assert row['kld']['value'] >= 0
            assert str(row['kld']['source']) in data['sources']
    assert len(data['bf16_benchmarks']) == 5


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', type=Path, default=DATA)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    data = json.loads(args.data.read_text(encoding='utf-8'))
    validate(data)
    rendered = outputs(data)
    if args.check:
        for file, fragment in [('README.md','summary'),('docs/index.html','html')]:
            text = (ROOT/file).read_text(encoding='utf-8')
            assert text.count(BEGIN) == text.count(END) == 1, file
            actual = text[text.index(BEGIN):text.index(END)+len(END)]
            assert actual == rendered[fragment], 'Stale generated section: ' + file
        assert (ROOT/'MODEL_COMPARISON.md').read_text(encoding='utf-8') == rendered['markdown']
        print('PASS: 50 unique variants, byte sums, source references, five BF16 benchmarks, README and site fragments match snapshot.')
    else:
        print(json.dumps(rendered, ensure_ascii=False))


if __name__ == '__main__':
    main()
