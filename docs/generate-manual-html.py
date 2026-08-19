# Regenerates the body of manual.html from manual.md, keeping the page shell (theme, sidebar,
# search, scripts) untouched. Requires: pip install markdown pygments
#
# The shell builds its own navigation from the headings at runtime, so the in-page table of
# contents is stripped. Headings get the same permalink markup and single-dash anchor ids as the
# original rendering, and a few pre-existing anchor ids for headings with <T> generics are pinned
# so published deep links keep working.
import io, os, re, markdown

docs = os.path.dirname(os.path.abspath(__file__))
md_path = os.path.join(docs, 'manual.md')
html_path = os.path.join(docs, 'manual.html')

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

h = io.open(html_path, encoding='utf-8', newline='').read()
start = h.index('<main id="main">') + len('<main id="main">')
end = h.index('</main>')
new = h[:start] + '\n' + body + '\n' + h[end:]
io.open(html_path, 'w', encoding='utf-8', newline='').write(new)
print('rendered %d chars of body, manual.html is now %d chars' % (len(body), len(new)))
