"""Static SVG overlays and section plots over matched native Blender renders."""

from html import escape
import json
from pathlib import Path


def fmt(value):
    return 'unavailable' if value is None else f'{value:.4f}'


def section_svg(data, title):
    from measure_math import dot, sub
    if not any(s['points'] for _, s in data):
        return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 100">'
                '<text x="10" y="30" fill="white">No observed contour: '
                + escape(title) + '</text></svg>')
    coords = [(key, s, [[dot(sub(p, s['point']), a) for a in s['axes']]
                       for p in s['points']]) for key, s in data]
    allpoints = [p for _, _, points in coords for p in points]
    low = [min(p[k] for p in allpoints) for k in range(2)]
    high = [max(p[k] for p in allpoints) for k in range(2)]
    scale = min(480/max(high[0]-low[0], .001), 300/max(high[1]-low[1], .001))

    def project(p):
        return (55+(p[0]-low[0])*scale, 360-(p[1]-low[1])*scale)

    lines = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 470" role="img" aria-label="{escape(title)}">',
             '<rect width="600" height="470" fill="#18232e"/>',
             f'<text x="20" y="25" fill="white">{escape(title)}</text>']
    for index, (key, s, points) in enumerate(coords):
        color = ['#ffbb62', '#66d8ff', '#acff99', '#d5aaff'][index]
        for segment in s['segments']:
            a, b = [project(points[i]) for i in segment['pointIds']]
            lines.append(f'<line x1="{a[0]}" y1="{a[1]}" x2="{b[0]}" y2="{b[1]}" '
                         f'stroke="{color}" stroke-width="2"/>')
        status = f"{key}: {s['origin']}; {len(s['components'])} contours; {s['status']}"
        lines.append(f'<text x="20" y="{405+index*16}" font-size="12" '
                     f'fill="{color}">{escape(status)}</text>')
    lines.append(f'<text x="55" y="385" fill="#ccc" font-size="12">axis 0: '
                 f'{low[0]:.3f} to {high[0]:.3f}; axis 1: {low[1]:.3f} to {high[1]:.3f}; raw units</text>')
    lines.append('</svg>')
    return '\n'.join(lines)


def write(report, profile, output):
    output = Path(output)
    headers = ['Measurement'] + [x['id'] for x in report['inputs']] + ['Review']
    rows, markdown = [], ['# Read-only character measurements', '',
                         'Offline mesh basis / rest-bone references; no animation or game qualification.',
                         '', '| ' + ' | '.join(headers) + ' |',
                         '|' + '|'.join('---' for _ in headers) + '|']
    for metric in report['comparisons']:
        values = metric['comparisons']
        units = 'degrees' if metric['kind'] == 'angle' else profile['frame']['units']
        notes = []
        for value in values:
            state = 'comparison unavailable' if value['review'] == 'unavailable' else value['review']
            notes.append(f"{value['input']}: {state}" +
                         (f" ({value['reason']})" if value['reason'] else '') +
                         (f"; delta {fmt(value['absoluteDelta'])}; {fmt(value['percentDelta'])}%"
                          if value['absoluteDelta'] is not None else ''))
        cells = [metric['label'] + ' (' + units + ')'] + [fmt(x['value']) for x in values]
        cells += ['; '.join(notes)]
        rows.append('<tr>' + ''.join('<td>' + escape(c) + '</td>' for c in cells) + '</tr>')
        markdown.append('| ' + ' | '.join(c.replace('|', '/') for c in cells) + ' |')
    with (output / 'report.md').open('x', encoding='utf-8') as f:
        f.write('\n'.join(markdown) + '\n')
    html = ['<!doctype html><html lang="en"><meta charset="utf-8"><title>Character measurements</title>',
            '<style>body{font:15px system-ui;background:#111a23;color:#e6edf5;margin:24px}'
            'table{border-collapse:collapse;width:100%}td,th{border:1px solid #485968;padding:8px;text-align:left}'
            '.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}'
            'img,svg{width:100%;max-width:750px}figure{margin:0}details{margin:12px 0}'
            'a{color:#77daff}h2{margin-top:32px}</style>',
            '<h1>Character measurements</h1><p>Offline mesh basis and rest-bone references. '
            'Source units are not assumed to be centimeters. Clothing estimates are not bare anatomy. '
            'Unavailable values stay unavailable; review warnings do not constitute import failures.</p>',
            '<table><tr>' + ''.join('<th>' + escape(h) + '</th>' for h in headers) + '</tr>',
            *rows, '</table><h2>Matched plain and annotated views</h2>']
    for view in profile['views']:
        html.append('<h3>' + escape(view['id']) + '</h3><div class="grid">')
        for input_data in report['inputs']:
            name = f"{input_data['id']}-{view['id']}"
            projection = input_data['projections'][view['id']]
            svg = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 750 750">',
                   f'<image href="{name}.png" width="750" height="750"/>']
            for i, (key, p) in enumerate(projection['landmarks'].items(), 1):
                source = input_data['landmarks'][key]
                color = '#ffbb62' if source['origin'] == 'clothing_estimate' else '#78eaff'
                svg.append(f'<circle cx="{p[0]}" cy="{p[1]}" r="4" fill="{color}"/>')
                svg.append(f'<text x="{p[0]+6}" y="{p[1]-6}" fill="{color}" '
                           f'stroke="#101820" stroke-width=".7" font-size="14">{i}</text>')
            for key, points in projection['sections'].items():
                for seg in input_data['sections'][key]['segments']:
                    a, b = [points[i] for i in seg['pointIds']]
                    svg.append(f'<line x1="{a[0]}" y1="{a[1]}" x2="{b[0]}" y2="{b[1]}" '
                               'stroke="#aaff9a" stroke-width="1.5"/>')
            svg.append('</svg>')
            with (output / (name + '-annotated.svg')).open('x', encoding='utf-8') as f:
                f.write('\n'.join(svg))
            html += [f'<figure><figcaption>{escape(input_data["id"])}</figcaption>',
                     '<details><summary>Plain matched view</summary>',
                     f'<img src="{name}.png" alt="Plain {escape(name)}"></details>',
                     '\n'.join(svg), '<ol>']
            for key in projection['landmarks']:
                lm = input_data['landmarks'][key]
                html.append('<li>' + escape(key + ': ' + lm['origin'] + '; ' + lm['note']) + '</li>')
            html += ['</ol></figure>']
        html.append('</div>')
    html.append('<h2>Actual triangle sections</h2><div class="grid">')
    for s in profile['sections']:
        svg = section_svg([(i['id'], i['sections'][s['id']]) for i in report['inputs']], s['label'])
        with (output / ('section-' + s['id'] + '.svg')).open('x', encoding='utf-8') as f:
            f.write(svg)
        html.append('<figure>' + svg + '</figure>')
    html += ['</div><h2>Landmark coordinates and selectors</h2>']
    for input_data in report['inputs']:
        html.append('<details><summary>' + escape(input_data['id']) + '</summary><pre>' +
                    escape(json.dumps(input_data['landmarks'], indent=2)) + '</pre></details>')
    html.append('</html>')
    with (output / 'report.html').open('x', encoding='utf-8') as f:
        f.write('\n'.join(html))
