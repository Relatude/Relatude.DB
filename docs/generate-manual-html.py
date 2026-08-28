# Regenerates the body of manual.html from manual.md and intro.html from intro.md, keeping the page
# shells (theme, sidebar, search, scripts) untouched. Requires: pip install markdown pygments
#
# Usage: python docs/generate-manual-html.py [manual|intro]   (default: both)
#
# The sidebar is driven by a baked "var NAV = [...]" array in the shell's script, which this
# script rebuilds from the rendered headings - so a new or renumbered chapter shows up in the
# navigation. The in-page table of contents is stripped from the HTML (the sidebar replaces it).
# Headings get the same permalink markup and single-dash anchor ids as the original rendering, and
# a few pre-existing anchor ids for headings with <T> generics are pinned so published deep links
# keep working.
import io, json, os, re, sys, markdown
from html import unescape

docs = os.path.dirname(os.path.abspath(__file__))
targets = sys.argv[1:] or ['manual', 'intro']
for name in targets:
    if name not in ('manual', 'intro'):
        raise SystemExit('unknown target "%s"; expected manual or intro' % name)


def render(name):
  md_path = os.path.join(docs, name + '.md')
  html_path = os.path.join(docs, name + '.html')

  md = io.open(md_path, encoding='utf-8', newline='').read()

  # the page builds its own navigation from the headings, so the in-page table of contents
  # is dropped, leaving the separator before Part I
  md = re.sub(r'## Table of contents.*?(?=# Part I )', '', md, flags=re.S)
  # collapse the double section rules; a lone pair renders as text + setext underline otherwise
  md = re.sub(r'\n---\r?\n---\r?\n', '\n---\n\n', md)

  body = markdown.markdown(md, extensions=['extra', 'codehilite', 'toc'])

  # permalink anchors in the shape the page css expects
  def add_anchor(m):
      tag, id_, text = m.group(1), m.group(2), m.group(3)
      return '<%s id="%s">%s<a class="anchor" href="#%s" aria-label="Link to this section">#</a></%s>' % (
          tag, id_, text, id_, tag)
  body = re.sub(r'<(h[1-6]) id="([^"]+)">(.*?)</\1>', add_anchor, body, flags=re.S)

  # the markdown's anchors are github-style (double dashes where punctuation was dropped between
  # spaces); the rendered ids are python-markdown slugs (runs collapse to one dash) - normalize
  # local fragment hrefs to match
  def fix_href(m):
      return 'href="#' + re.sub(r'-{2,}', '-', m.group(1)) + '"'
  body = re.sub(r'href="#([^"]+)"', fix_href, body)

  # the first rendering of the manual slugged headings with <T> generics differently (the <T> was
  # dropped as a tag); keep those published anchors stable for links from outside
  legacy_ids = {
      'embeddedt-a-collection-keyed-by-the-embedded-objects-own-guid-id': 'embedded-a-collection-keyed-by-the-embedded-objects-own-guid-id',
      'embeddedmaptkey-tvalue-a-collection-keyed-by-a-property-of-the-value': 'embeddedmap-a-collection-keyed-by-a-property-of-the-value',
      'reading-and-writing-a-referencet': 'reading-and-writing-a-reference',
      'reading-and-writing-a-referencest': 'reading-and-writing-a-references',
      'resultsett': 'resultset',
  }
  for new_id, old_id in legacy_ids.items():
      body = body.replace('id="%s"' % new_id, 'id="%s"' % old_id).replace('href="#%s"' % new_id, 'href="#%s"' % old_id)

  # the sidebar navigation: a nested [{level, text, id, children}] tree over h1-h3, baked into the
  # shell as "var NAV = [...]". Rebuilt here so it never drifts from the headings.
  def heading_text(inner_html):
      text = re.sub(r'<a class="anchor".*?</a>', '', inner_html, flags=re.S)
      text = re.sub(r'<[^>]+>', '', text)
      # the shell assigns these with textContent, so entities have to be resolved here
      # ("Data Modelling &amp; Querying" would otherwise show up as "&amp;" in the menu)
      return re.sub(r'\s+', ' ', unescape(text)).strip()

  nav = []
  stack = []  # (level, node)
  for tag, id_, inner in re.findall(r'<(h[1-3]) id="([^"]+)">(.*?)</\1>', body, flags=re.S):
      level = int(tag[1])
      node = {'level': level, 'text': heading_text(inner), 'id': id_, 'children': []}
      while stack and stack[-1][0] >= level:
          stack.pop()
      (stack[-1][1]['children'] if stack else nav).append(node)
      stack.append((level, node))

  h = io.open(html_path, encoding='utf-8', newline='').read()
  start = h.index('<main id="main">') + len('<main id="main">')
  end = h.index('</main>')
  new = h[:start] + '\n' + body + '\n' + h[end:]

  nav_start = new.index('var NAV = ')
  nav_end = new.index('\n', nav_start)
  new = new[:nav_start] + 'var NAV = ' + json.dumps(nav, ensure_ascii=False) + ';' + new[nav_end:]

  io.open(html_path, 'w', encoding='utf-8', newline='').write(new)
  print('%s.html: rendered %d chars of body, %d top level nav entries, %d chars total'
        % (name, len(body), len(nav), len(new)))


for name in targets:
    render(name)
