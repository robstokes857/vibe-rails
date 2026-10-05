"""Rebuild the TokenSaver research draft from published aggregates, without database access."""
from pathlib import Path
import sys, os, re, json, hashlib, math, csv, html
ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / '.deps'))
os.environ['MPLCONFIGDIR'] = str(ROOT / 'tmp' / 'matplotlib')
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
from datetime import datetime
from reportlab.pdfgen import canvas
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Image, Table, TableStyle, PageBreak, KeepTogether
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib import colors
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from pypdf import PdfReader

BOOKS = Path('C:/source/vibe-books')
PRODUCT = Path('C:/source/vibe-rails')
OUT = ROOT / 'output' / 'pdf'
FIG = ROOT / 'figures'
TMP = ROOT / 'tmp'
for d in (OUT, FIG, TMP): d.mkdir(parents=True, exist_ok=True)
DOSSIER = BOOKS / 'token_saver/research_2026-10-01.md'
RECENT = BOOKS / 'python-scripts/token_saver/results/codex-mine-0908'
source_text = DOSSIER.read_text(encoding='utf-8')
NAVY = '#17324d'; TEAL = '#087f8c'; BLUE = '#407bb0'; GOLD = '#c38b32'; GREY = '#9aa9b5'
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':9,'axes.spines.top':False,'axes.spines.right':False,
                     'axes.labelcolor':NAVY,'text.color':NAVY,'axes.edgecolor':'#bdc8d1','grid.color':'#e3e9ee',
                     'axes.titleweight':'bold','pdf.fonttype':42,'svg.fonttype':'none','savefig.facecolor':'white'})

# Exact transcription of published count tables; sizes in the source are rounded.
meter = [dict(agent='Claude Code',days=27,requests=16238,rewritten=8551,before_mb=7710,removed_mb=15.4,pct=0.20),
         dict(agent='Codex CLI',days=28,requests=16227,rewritten=15065,before_mb=9930,removed_mb=343,pct=3.45),
         dict(agent='Grok CLI',days=19,requests=1212,rewritten=658,before_mb=673,removed_mb=1.2,pct=0.18)]
tiers = [dict(group='Claude: all',n=35,explicit=3,exact=0,strict=16,loose=7,none=9),
         dict(group='Codex: classified',n=448,explicit=0,exact=0,strict=146,loose=53,none=249),
         dict(group='Codex: diff/show/log',n=284,explicit=0,exact=0,strict=122,loose=34,none=128),
         dict(group='Codex: grep',n=106,explicit=0,exact=0,strict=14,loose=14,none=78)]
daily=[]
for line in source_text.splitlines():
    if not re.match(r'^\| \d{2}-\d{2}', line): continue
    fields=[s.strip() for s in line.strip('|').split('|')]
    if len(fields)!=11: continue
    item={'date':'2026-'+fields[0][:5],'partial':'partial' in fields[0]}
    for j,name in ((1,'claude'),(6,'codex')):
        vals=[None if s=='—' else float(s.replace(',','')) for s in fields[j:j+5]]
        item[name]=dict(zip(['requests','rewritten','before_mb','removed_mb','pct'],vals))
    daily.append(item)
assert len(daily)==65
for row in tiers: assert sum(row[k] for k in ['explicit','exact','strict','loose','none']) == row['n']
study_a=[dict(agent='Claude',wire_chars=3337920,unique_chars=264898),dict(agent='Codex',wire_chars=51728364,unique_chars=1299384)]
for row in study_a: row['ratio']=row['wire_chars']/row['unique_chars']
late={}
for name in ('claude','codex'):
    rows=[d[name] for d in daily if d['date']>='2026-09-04' and d[name]['requests'] is not None]
    sums={k:sum(r[k] for r in rows) for k in ['requests','rewritten','before_mb','removed_mb']}
    sums['weighted_pct']=100*sums['removed_mb']/sums['before_mb']; sums['active_days']=len(rows); late[name]=sums
derived={'strict_claude_pct':100*19/35,'strict_codex_pct':100*146/448,'strict_codex_diff_pct':100*122/284,
         'unresolved_lower_pct':100*146/467,'unresolved_upper_pct':100*165/467,
         'illustrative_gross_units':1000*(1.25+19*0.1),'illustrative_break_even':0.315}
data={'meter_initial_snapshot':meter,'daily_later_snapshot':daily,'later_rounded_row_totals':late,
      'followup_counts':tiers,'study_a_characters':study_a,'derived':derived,
      'units_note':'Meter: reported UTF-8 request-body bytes. Study A: characters. Tier counts: result instances. Daily sizes rounded.'}
recent_summary=json.loads((RECENT/'summary.json').read_text(encoding='utf-8'))
recent=[]
agent_labels={'anthropic':'Claude Code','openai':'Codex CLI','cli-chat':'Grok CLI'}
for row in recent_summary['requests']:
    if row[1]!=200:continue
    recent.append({'provider':row[0],'agent':agent_labels[row[0]],'requests':row[2],
                   'before_bytes':row[3],'removed_bytes':row[4],'pct':100*row[4]/row[3]})
recent.sort(key=lambda r:['anthropic','openai','cli-chat'].index(r['provider']))
assert sum(r['requests'] for r in recent)==8286
actual_usage={k:0 for k in ['input_tokens','cache_creation_input_tokens','cache_read_input_tokens','output_tokens',
 'cache_creation.ephemeral_5m_input_tokens','cache_creation.ephemeral_1h_input_tokens']}
for key,usage in recent_summary['usage'].items():
    if "'anthropic'" in key:
        for field in actual_usage:actual_usage[field]+=usage.get(field,0)
assert actual_usage['cache_creation.ephemeral_5m_input_tokens']+actual_usage['cache_creation.ephemeral_1h_input_tokens']==actual_usage['cache_creation_input_tokens']
archive_replay={'variants':8177,'configurations':4,'cases':32708,
 'exceptions':0,'character_growth':0,'canonical_json_byte_growth':0,'nondeterministic':0,'nonidempotent':0,
 'historical_product_commit':'2d4ee1828f4fae8d513fb577e2bf36f6300ef04b',
 'input_note':'Recorded after-text, with command correlation; UTF-16 string length in the C# replay. Historical replay, not rerun today.'}
file_read_replay={'sample_n':261,'detected_for_protection':205,'other_outputs':56,
 'original_removed_chars':4056231,'removal_associated_with_detected_reads':2960996,
 'wider_sample_n':288,'wider_detected':217,'wider_associated_chars':3153691}
data['september8_bounded_requests']=recent
data['september8_replay']=archive_replay
data['september8_claude_reported_usage']=actual_usage
data['historical_file_read_replay']=file_read_replay
(ROOT/'evidence.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
with (ROOT/'daily-series.csv').open('w',newline='',encoding='utf-8') as f:
    w=csv.writer(f); w.writerow(['date','partial','agent','requests','rewritten','before_MB','removed_MB','reported_percent'])
    for d in daily:
        for agent in ('claude','codex'): w.writerow([d['date'],d['partial'],agent,*d[agent].values()])

def savefig(name,fig):
    fig.savefig(FIG/f'{name}.png',dpi=220,bbox_inches='tight')
    fig.savefig(FIG/f'{name}.pdf',bbox_inches='tight')
    fig.savefig(FIG/f'{name}.svg',bbox_inches='tight')
    plt.close(fig)

fig,ax=plt.subplots(figsize=(6.6,2.15),layout='constrained')
ax.barh([2,1,0],[.20,3.45,.18],color=[TEAL,BLUE,GREY],height=.55)
ax.set_yticks([2,1,0],['Claude Code','Codex CLI','Grok CLI']); ax.set_xlim(0,4.1)
ax.set_xlabel('Request-body bytes removed (%)'); ax.xaxis.grid(True); ax.set_axisbelow(True)
for y,v in zip([2,1,0],[.20,3.45,.18]): ax.text(v+.06,y,f'{v:.2f}%',va='center',weight='bold')
savefig('01-observed-wire',fig)

fig,axes=plt.subplots(2,1,figsize=(6.6,3.0),sharex=True,layout='constrained')
dates=[datetime.fromisoformat(d['date']) for d in daily]
for ax,key,c,label in zip(axes,['claude','codex'],[TEAL,BLUE],['Claude Code','Codex CLI']):
    vals=[np.nan if d[key]['pct'] is None else d[key]['pct'] for d in daily]
    ax.plot(dates,vals,color=c,lw=1.2,marker='o',ms=2); ax.set_ylabel('Bytes removed (%)'); ax.set_title(label,loc='left',fontsize=9)
    ax.grid(axis='y'); ax.set_ylim(bottom=0)
    for date in ['2026-08-18','2026-08-29']: ax.axvline(datetime.fromisoformat(date),color=GOLD,lw=.8,ls='--')
axes[0].text(datetime(2026,8,18),3.5,'exec allowlisting',fontsize=7,ha='center')
axes[0].text(datetime(2026,8,29),2.95,'file-read protection',fontsize=7,ha='left')
axes[0].set_ylim(0,4.2);axes[1].set_ylim(0,31)
axes[1].xaxis.set_major_locator(mdates.WeekdayLocator(interval=2));axes[1].xaxis.set_major_formatter(mdates.DateFormatter('%b %d'))
savefig('02-daily-series',fig)

fig,axes=plt.subplots(1,2,figsize=(6.6,2.45),layout='constrained')
for ax,row in zip(axes,study_a):
    vals=[row['unique_chars']/1e6,row['wire_chars']/1e6]
    ax.bar(['Distinct content','With resends'],vals,color=[TEAL,BLUE],width=.55)
    ax.set_title(f"{row['agent']}: {row['ratio']:.1f}x replay weighting",loc='left',fontsize=10)
    ax.set_ylabel('Million characters removed'); ax.grid(axis='y'); ax.set_axisbelow(True); ax.set_ylim(0,max(vals)*1.23)
    for x,v in enumerate(vals): ax.text(x,v+max(vals)*.025,f'{v:.3f}',ha='center',fontsize=9)
savefig('03-resend-amplification',fig)

fig,ax=plt.subplots(figsize=(6.6,2.15),layout='constrained')
m=np.arange(1,41)
ax.plot(m,m,color=GREY,label='Raw token appearances: m',lw=1.8)
for a,c in [(.1,BLUE),(.05,TEAL),(.025,GOLD)]: ax.plot(m,1.25+a*(m-1),color=c,label=f'Illustration: alpha = {a:g}',lw=1.5)
ax.set_xlabel('Appearances of the same removed block (m)'); ax.set_ylabel('Multiplier per removed token')
ax.legend(frameon=False,fontsize=7,loc='upper left');ax.grid();ax.set_xlim(1,40);ax.set_ylim(0,42)
savefig('04-cache-weighting',fig)

fig,ax=plt.subplots(figsize=(6.6,2.55),layout='constrained')
left=np.zeros(4); yy=np.arange(4)
for key,label,c in [('explicit','T0: explicit + related',TEAL),('strict','T2: strict related',BLUE),('loose','T3: loose',GOLD),('none','No classified relation','#d9e2e8')]:
    vals=np.array([100*r[key]/r['n'] for r in tiers]);ax.barh(yy,vals,left=left,color=c,label=label,height=.62)
    left+=vals
ax.set_yticks(yy,[f"{r['group']} (n={r['n']})" for r in tiers]);ax.invert_yaxis();ax.set_xlim(0,100)
ax.set_xlabel('Share of classified truncation instances (%)')
ax.legend(frameon=False,loc='upper center',bbox_to_anchor=(.40,-.30),ncol=2,fontsize=7)
savefig('05-followup-tiers',fig)

fig,ax=plt.subplots(figsize=(6.6,2.75),layout='constrained')
q=np.linspace(0,1,201)
for k,c in [(1,TEAL),(2,BLUE),(4,GOLD)]:ax.plot(q,1-k*q,label=f'C / G = {k}',color=c,lw=1.7)
ax.axhline(0,color=NAVY,lw=.8)
ax.set_xlabel('Probability of a causally additional recovery episode (q)');ax.set_ylabel('Expected net / gross saving')
ax.legend(frameon=False,loc='lower left',fontsize=8);ax.grid();ax.set_xlim(0,1);ax.set_ylim(-1.1,1.1)
savefig('06-break-even',fig)

fig,ax=plt.subplots(figsize=(6.6,2.15),layout='constrained')
ax.barh([2,1,0],[r['pct'] for r in recent],color=[TEAL,BLUE,GREY],height=.55)
ax.set_yticks([2,1,0],[r['agent'] for r in recent]);ax.set_xlim(0,1.7)
ax.set_xlabel('Recorded UTF-8 request-body bytes removed (%)');ax.xaxis.grid(True);ax.set_axisbelow(True)
for y,row in zip([2,1,0],recent):ax.text(row['pct']+.025,y,f"{row['pct']:.3f}%",va='center',weight='bold')
savefig('07-bounded-september-audit',fig)

fig,axes=plt.subplots(1,2,figsize=(6.6,2.4),layout='constrained')
for ax,vals,title,ylabel in [
 (axes[0],[205,56],'Historical 261-output sample','Output instances'),
 (axes[1],[2960996/1e6,1095235/1e6],'Associated pre-fix removal','Million decoded characters')]:
    ax.bar(['Detected file reads','Other outputs'],vals,color=[TEAL,GREY],width=.58)
    ax.set_title(title,fontsize=10,loc='left');ax.set_ylabel(ylabel);ax.set_ylim(0,max(vals)*1.22)
    ax.grid(axis='y');ax.set_axisbelow(True);ax.tick_params(axis='x',labelsize=8)
    for x,v in enumerate(vals):ax.text(x,v+max(vals)*.025,f'{v:g}' if ax is axes[0] else f'{v:.3f}',ha='center')
savefig('08-file-read-mitigation',fig)

fig,axes=plt.subplots(1,2,figsize=(6.6,2.6),layout='constrained')
u=actual_usage
vals=[u['input_tokens']/1e6,u['cache_creation_input_tokens']/1e6,u['cache_read_input_tokens']/1e6]
axes[0].bar(['Uncached','Cache writes','Cache reads'],vals,color=[GOLD,TEAL,BLUE]);axes[0].set_ylabel('Million reported input tokens')
axes[0].set_title('Claude: 6,209 successful requests',fontsize=9,loc='left');axes[0].set_ylim(0,640)
for x,v in enumerate(vals):axes[0].text(x,v+10,f'{v:.3f}',ha='center',fontsize=8)
wvals=[u['cache_creation.ephemeral_5m_input_tokens']/1e6,u['cache_creation.ephemeral_1h_input_tokens']/1e6]
axes[1].bar(['5-minute','1-hour'],wvals,color=[TEAL,GOLD]);axes[1].set_ylabel('Million cache-write tokens');axes[1].set_ylim(0,12)
axes[1].set_title('Cache writes by recorded bucket',fontsize=9,loc='left')
for x,v in enumerate(wvals):axes[1].text(x,v+.2,f'{v:.3f}',ha='center',fontsize=8)
for ax in axes:ax.grid(axis='y');ax.set_axisbelow(True);ax.tick_params(axis='x',labelsize=8)
savefig('09-actual-usage-buckets',fig)

fontdir=ROOT/'.deps/matplotlib/mpl-data/fonts/ttf'
for name,file in [('Paper','DejaVuSerif.ttf'),('Paper-Bold','DejaVuSerif-Bold.ttf'),('Sans','DejaVuSans.ttf'),('Sans-Bold','DejaVuSans-Bold.ttf'),('Sans-Oblique','DejaVuSans-Oblique.ttf')]:pdfmetrics.registerFont(TTFont(name,str(fontdir/file)))
pdfmetrics.registerFontFamily('Paper',normal='Paper',bold='Paper-Bold',italic='Paper',boldItalic='Paper-Bold')
pdfmetrics.registerFontFamily('Sans',normal='Sans',bold='Sans-Bold',italic='Sans-Oblique',boldItalic='Sans-Bold')

pages=[]; current=[]
def page(title):
    global current
    current=[];pages.append((title,current))
def p(t):current.append(('p',t))
def h(t):current.append(('h',t))
def equation(tex,number):current.append(('eq',tex,number))
def table(headers,rows,widths=None):current.append(('table',headers,rows,widths))
def figure(name,caption,width=470):current.append(('fig',name,caption,width))
def note(t):current.append(('note',t))

page('Measuring TokenSaver Compression Savings')
current.append(('subtitle','Byte-level guarantees, cache-aware economics, and observed agent behavior'))
note('RESEARCH DRAFT 0.2  |  5 OCTOBER 2026  |  Prepared for Robert Stokes')
h('Abstract')
p('TokenSaver rewrites selected tool-result strings before coding-agent requests reach a model provider. This paper examines whether those rewrites constitute useful savings. It combines a 65-day dossier, additional archived studies, a current source audit, nine figures, and formal accounting and correctness arguments. The first October 1 meter snapshot reports reductions of <b>3.45% of request-body bytes for Codex CLI</b>, <b>0.20% for Claude Code</b>, and <b>0.18% for Grok CLI</b>. These are measurements of transmitted request bodies, not net billing-token effects. [1]')
p('Repeated history explains why wire removal exceeds distinct-content removal by 12.60x and 39.81x in two historical samples. The corrected follow-up audit finds a related call in the next assistant turn after 19/35 Claude truncations and 146/448 classified Codex truncations. These associations do not establish that truncation caused extra turns. A source-level proof establishes byte non-growth for accepted replacements, and 475 focused tests passed on October 5. A second proof characterizes exactly when a compressed observation can preserve a task answer; arbitrary truncation cannot satisfy that condition for every task.')
p('The evidence establishes gross transport reduction and specific engineering guarantees. It does not yet establish net billed-token savings or noninferior task success. We derive the break-even conditions and specify a paired experiment that can test both, without converting character counts or heuristic follow-ups into unsupported token claims.')
table(['Claim','Evidence status'],[
    ['Smaller transmitted request bodies','Measured; provider and snapshot specific'],
    ['Accepted rewrite cannot enlarge the body','Proved under the splice model; source checked'],
    ['Stable rewriting and protocol behavior','Supported by focused regression tests'],
    ['Lower total task cost with equal quality','Open empirical question; protocol specified']],[155,315])
note('Revision 0.2 adds a bounded September audit of 8,286 successful requests, a historical C# replay of 8,177 output variants under four configurations, actual usage buckets, and the file-read mitigation study. Historical replay evidence is kept separate from the 475 tests run for this draft.')

page('1. System and study design')
p('TokenSaver is a local proxy with provider-specific JSON rewriters. It correlates tool calls with their results, selects eligible text, runs deterministic cleanup, recognized-command reshaping and condensation stages, and accepts a replacement only if its serialized byte representation is smaller. The studied design passes provider responses through unchanged. System instructions, user messages and tool-call inputs are outside the selected tool-result spans. [1, 3]')
table(['Mechanism','Current implementation boundary'],[
 ['Eligibility','Allowlisted shell results; dedicated Read/Grep scopes default off. Shell results can still contain source files.'],
 ['Cleanup','Configured line-ending, trailing-space and blank-line normalization; not universal semantic equivalence.'],
 ['Run collapse','At least three identical consecutive lines; replace only when the marker is smaller.'],
 ['Truncation','Normally retain 150 head and 50 tail lines plus an elision marker.'],
 ['File-read mitigation','Recognized file reads use 1,200 head and 200 tail lines if the post-dedupe text has at most 262,144 UTF-16 code units.'],
 ['Activation threshold','Omitted middle must contain at least 10 lines and 4,096 UTF-16 code units. This is not a token or byte cap.']],[105,365])
h('Populations and time windows')
p('The dossier covers one developer\'s workstation from July 29 through October 1, 2026. Its derived sequence dataset contains 65,582 requests and 4,015 conversation keys; a separate historical corpus contains 49,851 content-hash-distinct tool outputs. These populations are not interchangeable. Conversation keys are derived from opening messages, and the sequence extractor retains the request with the most tool calls per conversation. Branches and segments after compaction can be missed. [1, sections 3-5]')
p('The primary meter table uses activity from September 4 through the first October 1 snapshot. The daily figure uses a later October 1 reread. The follow-up classifier uses conversations first observed on or after August 30. Study A\'s distinct-content comparison uses August 28 for Claude and August 28-29 for Codex. Each figure states its own denominator and window.')
note('Current source audit: vibe-rails 336373c0c51b2dc68abb3350910cee533502651a. Historical research checkout: vibe-books cf3253b99e6992e7b62564bfc3fb23f276a0e39c. File hashes accompany the draft. No raw request bodies were copied into the paper.')

page('2. Accounting and a transport guarantee')
p('Let B_j and A_j denote the UTF-8 byte lengths of the original and actually forwarded request body j. The gross transport reduction is the sum of accepted body differences. The byte-weighted share uses the sum of original bytes as its denominator; an unweighted mean of daily percentages estimates a different quantity.')
equation(r'D_B=\sum_{j=1}^{J}(B_j-A_j),\qquad S_B=\frac{D_B}{\sum_{j=1}^{J}B_j}',1)
p('For a fixed provider model m, let tau_m(x) be its input-token counting function. A paired request-level token difference requires counting both complete original and transformed requests with that same model. A byte reduction does not imply a token reduction: tokenization is not monotone in string byte length, and model-specific framing also matters. The TokenSaver savings field instead uses floor(BytesSaved / 4), an explicit estimate. [4, 7]')
equation(r'D_T=\sum_{j=1}^{J}\left[\tau_m(B_j^{\rm body})-\tau_m(A_j^{\rm body})\right]',2)
h('Proposition 1. Accepted splices cannot enlarge the body')
p('Assume the parser identifies disjoint eligible JSON string spans s_i and untouched intervening spans v_i. Let c_i be an encoded candidate replacement, and let a_i equal c_i only when its UTF-8 byte length is strictly smaller than that of s_i; otherwise a_i equals s_i. The rewrite copies each v_i without modification. Then:')
equation(r'|R(B)|=\sum_i|v_i|+\sum_i|a_i|\ \leq\ \sum_i|v_i|+\sum_i|s_i|=|B|',3)
p('<b>Proof.</b> Every accepted span satisfies |a_i| &lt; |s_i|, every rejected span satisfies equality, and disjoint concatenated byte lengths add. At least one accepted replacement makes the total inequality strict. All bytes outside accepted spans retain their original values. The three provider rewriters enforce the encoded-length comparison at the source locations listed in reference 3.')
p('This is a guarantee about the implemented splice contract, conditional on correct parsing and copying. It does not prove that every tool-result meaning is preserved, that tokenization shrinks, or that a complete task requires fewer requests. Candidate stage traces can include rejected rewrites; actual savings must be conditioned on RewriteAccepted or derived from the forwarded body.')

page('3. Distinct content, replay, and cache weighting')
p('For distinct removed result content i, let d_i be the removed character count and m_i its number of appearances in subsequent requests. Under stable rewriting, D_unique = sum d_i and D_wire,char = sum m_i d_i. Their ratio is a removal-weighted mean replay count, not a task-saving multiplier. [1, section 6.3]')
figure('03-resend-amplification','Figure 1. Historical Study A: character counts with and without replay weighting. Claude: August 28; Codex: August 28-29. Distinct content is content-hash deduplicated here. The two panels use different vertical scales.',455)
p('Repeated input can be charged at a cache-read rate. For a block of d actual tokens, first-write multiplier w, read multiplier alpha, and m appearances, its gross avoided input cost is d[w + alpha(m - 1)] in units of the base input price. This identity assumes unchanged future requests, token counts, cache hits and cache boundaries. It is a conditional accounting model, not an observed net effect.')
figure('04-cache-weighting','Figure 2. Illustrative accounting with w = 1.25 and fixed cache-read multipliers. At m = 40 and alpha = 0.10, the cost multiplier is 5.15, versus 40 raw token appearances. These curves are not fitted to study traffic.',455)
note('Current provider documentation distinguishes cache-write and cache-read prices and includes model-specific exceptions. Rates must be pinned per model and date before calculating money. The study\'s historical provider-wide factors are not used here to claim current dollar savings. [6]')

page('4. Measured request-body reduction')
figure('01-observed-wire','Figure 3. Reported gross request-body byte reduction in the first October 1 meter snapshot, activity since September 4. Denominators: Claude 16,238 requests; Codex 16,227; Grok 1,212. Sizes in the dossier are rounded; percentages are reproduced as reported. [1, section 6.1]',450)
table(['Agent','Original body data','Removed','Share'],[['Claude Code','7.71 GB','15.4 MB','0.20%'],['Codex CLI','9.93 GB','343 MB','3.45%'],['Grok CLI','673 MB','1.2 MB','0.18%']],[110,140,110,110])
figure('02-daily-series','Figure 4. Daily reported byte-reduction percentages from the later October 1 meter reread. Missing Claude days remain gaps; October 1 is partial. Dashed lines mark reported rollout dates, not randomized interventions. Axis scales differ by panel. [1, Appendix A.1]',455)
p('The later daily ledger gives approximately 0.19% for Claude and 3.39% for Codex when rounded daily byte totals are summed before division. Those later totals are not combined with the earlier Figure 3 counts. Provider mix, harness behavior and workload changed over time; the rollout annotations alone do not identify causal savings.')

page('5. What agents do after truncation')
p('The corrected audit assigns the strongest related-call tier found in the next assistant turn. T0 requires both an explicit acknowledgment of incomplete output and a related call; T1 requires an exact command repeat; T2 requires stronger path/range evidence; T3 retains a looser relation. T1 is zero in the plotted groups. These are classifier outputs, not observed extra-turn causes. [1, section 7.2; 2]')
figure('05-followup-tiers','Figure 5. Next-turn related-call classification after truncation. Codex excludes 19 of 467 unresolved command wrappers. Diff/show/log and grep are subsets of the Codex classified group; they are not additional independent populations.',460)
table(['Population','Strict T0-T2','Including loose T3'],[['Claude, all truncations','19 / 35 = 54.29%','26 / 35 = 74.29%'],['Codex, classified','146 / 448 = 32.59%','199 / 448 = 44.42%'],['Codex, diff/show/log','122 / 284 = 42.96%','156 / 284 = 54.93%'],['Codex, grep','14 / 106 = 13.21%','28 / 106 = 26.42%']],[155,155,160])
p('Treating all 19 unresolved Codex cases as negative or positive bounds the full-sample strict fraction between 146/467 = <b>31.26%</b> and 165/467 = <b>35.33%</b>. This is a sensitivity bound for missing classifications only. It does not cover classifier error, missed later recovery, missing conversation branches, or causality.')
p('The audit also reports 318 thousand removed characters and 152 thousand follow-up-output characters for Claude; Codex reports 8,255 thousand and 1,860 thousand. These are decoded-character sums over marked result instances in retained timelines, before replay weighting. They are not guaranteed globally distinct content. The follow-up may contain different information, and may have occurred anyway. Subtracting these columns would not measure net savings.')
note('No population confidence intervals are attached to these counts: result instances cluster within conversations, the workload comes from one developer, and the classifier itself is imperfect. Silence after truncation is not evidence that the omitted content was irrelevant.')

page('6. The economics of recovery')
p('Let G be the gross avoided input cost under a fixed baseline continuation, q the probability that compression causally adds a recovery episode, C the expected incremental cost conditional on such an episode, and K other incremental costs, including cache changes. All costs use the same currency or a fixed base-price unit. Under this explicitly simplified model:')
equation(r'E[N]=G-qC-K,\qquad q^*=\frac{G-K}{C}\quad(C>0)',4)
p('Positive expected net savings require q &lt; q*. A threshold below zero means recovery is not needed to erase the gross benefit; a threshold above one means the modeled gross benefit exceeds all possible recovery incidence at the assumed C. The formula is algebra, not an estimate of q or C. The audit\'s related-call frequency is not q.')
figure('06-break-even','Figure 6. Hypothetical break-even sensitivity: normalized net = 1 - q(C/G), with K = 0. Lines vary the assumed recovery cost. No observed follow-up rate is plotted on these curves; the causal probability and incremental cost have not been measured.',455)
h('A worked example, with assumptions exposed')
p('Assume a removed block has 1,000 actual model tokens, appears 20 times, is first written at w = 1.25, and is read 19 times at alpha = 0.10. Gross avoided input cost is G = 1,000(1.25 + 19 x 0.10) = <b>3,150 base-price units</b>. If one causally additional recovery episode costs 10,000 of those units and K = 0, then q* = 0.315. At q = 0.10, expected net is +2,150; at q = 0.50 it is -1,850. These values are illustrative, not study measurements.')
p('For actual bills, use the response usage categories and the price schedule effective at each call. Write total task cost as the sum over requests of p_uncached U + p_write W + p_read R + p_output O, plus any other billed fees. Model versions, caching tiers, long-context rules and subscription accounting must be recorded. A byte/4 meter cannot supply those terms.')

page('7. What correctness can mean')
h('Proposition 2. Exact task preservation has a necessary and sufficient condition')
p('Let x be a full tool observation, C(x) its compressed form, and g(x) the correct answer to a specified task. There exists a decoder h that recovers the answer from the compressed form exactly when g is constant on each set of inputs that C maps to the same output:')
equation(r'\exists h:\ h(C(x))=g(x)\ \forall x\quad\Longleftrightarrow\quad C(x)=C(y)\Rightarrow g(x)=g(y)',5)
p('<b>Proof.</b> Necessity: if C(x) = C(y), the decoder receives the same input, so h must return the same answer. Sufficiency: for each compressed output z, define h(z) to be the common g-value of any input mapped to z. Constancy makes this definition unambiguous.')
p('<b>Counterexample for unrestricted truncation.</b> Choose two long outputs with identical retained head and tail, equal omitted line counts, and different omitted middle facts. A question about those facts has different correct answers, yet the compressed strings and markers can be identical. Therefore arbitrary lossy truncation cannot preserve every possible task. Even when an answer is preserved, a particular LLM may fail to extract it. A task-outcome comparison remains necessary.')
h('Implementation evidence collected for this draft')
p('<b>475 tests passed, 0 failed, 0 skipped</b> in a focused run of ten existing TokenSaver test classes on October 5, 2026, using .NET SDK 10.0.401. The selection covered cleanup, condensation, golden fixtures, command shaping, file-read budgets, line endings, and all three provider rewriters. The shared tree contained unrelated edits; no TokenSaver or Tests/TokenSaver edits were present before the run. The exact selection is in validation.json. [5]')
table(['Property','Evidence and scope'],[
 ['Byte non-growth and untouched spans','Encoded-length gates in three rewriters; provider regression tests.'],
 ['Determinism and idempotence','Fixed configuration; cleanup flag combinations, condenser combinations and pipeline composition fixtures.'],
 ['Protocol preservation','Provider-specific parsing/rewriting fixtures and original-byte fallback.'],
 ['Downstream task quality','Not established by these tests or by the historical trace audit.']],[145,325])
note('Idempotence of individual stages does not imply idempotence of their composition. The suite explicitly includes composition cases. Historical research also reports a 108-combination sweep and zero compression-attributed divergences in 64 investigated close-in-time cache misses; those are bounded samples, not universal guarantees. [1, sections 5.6 and 8.2]')

page('8. A bounded September request audit')
p('The expanded archive search found a September 8 mining report and its saved aggregates outside the main dossier, in an ignored experiment directory. Its extraction fixed the lower date at September 4 and the upper source row at 61,143, ending at 17:33:05 UTC on September 8. It retained 8,286 HTTP-200 model requests and 10 unsuccessful requests; model-list and count-token endpoints were excluded. The table and figure below use successful requests only. [9]')
figure('07-bounded-september-audit','Figure 7. A separate bounded population: observed original-minus-forwarded UTF-8 request-body bytes. These exact saved aggregate counts produce the plotted rates. The interval overlaps Figure 3; do not pool the two populations or treat this as an independent controlled replication.',440)
table(['Agent / requests','Original bytes','Removed bytes','Rate'],[
 [f"{r['agent']} / {r['requests']:,}",f"{r['before_bytes']:,}",f"{r['removed_bytes']:,}",f"{r['pct']:.3f}%"] for r in recent],[140,130,125,75])
h('Production-source replay on recorded outputs')
p('The saved study replayed <b>8,177 distinct provider/tool/command/raw/after variants</b> through C# pipeline source from commit 2d4ee182. Four configurations covered defaults, ordinary cleanup, cleanup plus ANSI removal, and defaults plus ANSI removal: <b>32,708 variant/configuration cases</b>. The saved flags show zero character-growth, canonical-JSON-byte-growth, nondeterminism or idempotence failures, and the completed run reports no exceptions. This is historical corpus evidence, not a replay performed for this revision. [9]')
p('Inputs were recorded <b>after-texts</b>, with command metadata; character growth uses UTF-16 code units. Some replayed tool types were outside the live allowlist. The helper exercises the pipeline, not the complete HTTP adapter; canonical JSON is not each producer\'s escaping. Including after-text in the key preserves 28 raw-output groups with multiple transformed versions, which the older corpus could overwrite. This does not establish task success.')

page('9. File-read preservation and stage interactions')
p('An earlier investigation discovered that shell-based file reads were being truncated despite dedicated file-read tools being out of scope. The ensuing mitigation widened the line budget for detected file reads. Offline replay of the production predicate over 261 previously elided outputs detected 205 for protection (78.54%); those outputs accounted for 2,960,996 of 4,056,231 characters removed before the fix (73.00%). [10]')
figure('08-file-read-mitigation','Figure 8. Historical detector replay, captured traffic August 26-29. The left panel counts outputs; the right associates their historical removal with the detector decision. This is mitigation coverage, not newly saved tokens or a task-quality effect. The panels use different units.',450)
p('The August 30 rerun reproduced 205/261 on the same sample. An expanded 288-output sample detected 217 (75.35%), associated with 3,153,691 characters of historical removal; the reported size ceiling excluded none of these 217. These are different denominators, so the percentage change is not evidence of a detector regression. The source notes that no live soak had yet been completed. [10]')
h('Why an apparently safe stage needs a complete-pipeline check')
p('The September 4 review found that an ANSI-removal Python mirror used outdated blank-line and file-read rules. After correction, its opportunity estimate was about 22.69 million replay-weighted characters: 9.63 million from ANSI plus ordinary cleanup and 13.06 million from newly enabled lossy condensation. Those are candidate replay estimates, not measured wire savings. A stage that removes control codes can enable downstream truncation; the entire resulting change cannot be credited to harmless formatting cleanup. [11]')
p('The bounded September 8 production replay found only 4,830 additional replay-weighted UTF-16 code units from Codex ANSI cleanup over ordinary cleanup, across six outputs. A dedicated Read cleanup candidate offered 28,391 weighted code units for Claude. These are candidate opportunities in a different window, not comparable realized savings. They support testing each proposed expansion against the actual pipeline. [9]')

page('10. Actual token usage is available, with limits')
p('The September 8 extraction preserved provider-reported usage for every successful Claude request. This supplies a firmer accounting basis than characters divided by four. The following values cover the 6,209 Claude requests in Figure 7, including main and side requests. The categories describe actual usage under the observed system; there is no matching uncompressed token counterfactual. [9]')
figure('09-actual-usage-buckets','Figure 9. Actual provider-reported usage in the bounded September audit. Left: disjoint input categories; right: the cache-write category split by duration. Panels use different scales. Output tokens are reported separately in the table and are not included in the input bars.',450)
table(['Reported category','Tokens'],[
 ['Uncached input',f"{u['input_tokens']:,}"],['5-minute cache writes',f"{u['cache_creation.ephemeral_5m_input_tokens']:,}"],
 ['1-hour cache writes',f"{u['cache_creation.ephemeral_1h_input_tokens']:,}"],['Cache reads',f"{u['cache_read_input_tokens']:,}"],
 ['Total input',f"{u['input_tokens']+u['cache_creation_input_tokens']+u['cache_read_input_tokens']:,}"],
 ['Output',f"{u['output_tokens']:,}"]],[310,160])
p('Duration buckets matter when pricing writes. A historical classifier-request family in the same study reported 4,634,900 cache-write tokens, all in the one-hour bucket. A blanket first-write factor of 1.25 cannot describe that family under a schedule that charges one-hour writes at a different rate. The illustrative equations in this paper keep rates explicit; a realized bill comparison must also retain each model\'s price and effective date. [6, 9]')
p('The archive reports usage missing from 100 successful Codex responses and 9 successful Grok responses in this bounded sample. Usage composition for those consumers is therefore incomplete. Across all providers, exact observed usage alone still cannot attribute the difference caused by compression: the paired task experiment must supply that missing comparison.')

page('11. Source reconciliation and uncertainty')
p('Several source phrases require stricter definitions before use in a paper. The draft preserves the historical findings while correcting the following interpretations. These corrections are measurement clarifications; no product changes or raw-data re-extraction were performed.')
table(['Source phrase or shortcut','Treatment in this paper'],[
 ['"Tokens saved" from BytesSaved / 4','Estimated tokens only. Actual model token counts and billable usage are separate.'],
 ['Audit KB / MB values','Thousands / millions of decoded characters where the scripts use Python len(text). Meter bytes remain UTF-8 bytes.'],
 ['"Unique" tiered-audit removal','Marked result instances from one retained timeline per conversation; no global content-hash deduplication.'],
 ['11.2 MB = 0.23% of 6,154 MB','Not a valid equality: the script uses tool-result character delta for the size, but serialized-request character delta for the percentage. Omitted from the figures.'],
 ['Single October 1 total','Initial meter and later daily reread are distinct snapshots.'],
 ['"Refetch cost"','A related next-turn call is observed; causally additional work and recovered content are unknown.'],
 ['"Lossless" cleanup','Normalization under restricted text assumptions; not byte reversibility or all-language semantic preservation.']],[145,325])
h('Threats to validity')
p('<b>Selection and dependence.</b> One developer and workstation supply the traffic. Provider, task, model and harness differences are entangled. Requests and results are correlated within tasks. The authors of the original analysis were themselves coding agents whose traffic entered the dataset. The observation period was not randomized.')
p('<b>Incomplete trajectories and capture.</b> Conversation hashing and maximum-tool-count selection can omit branches and post-compaction segments. Command summaries stop at 400 characters; follow-up detection examines only the next turn. A separate backup audit documents possible capture loss, body-size limits and no durable inventory of capture gaps. Recorded traffic is not guaranteed to include every request. [12]')
p('<b>Unknown counterfactuals.</b> The logs do not reveal the uncompressed task trajectory, recovered information, incremental recovery cost or eventual task quality. Historical cost models used a loose classifier and a subsequently corrected output-price factor. Their net estimates are not reproduced as results here.')
p('<b>Freshness.</b> Current source checks and passing tests do not remeasure July-October workloads under today\'s configuration. No evidence here supports universal, product-wide or future-model savings rates.')

page('12. An experiment that can establish net savings')
p('Compare complete tasks under a fixed harness, frozen repository, explicit acceptance criteria and prespecified analysis. The primary questions are whether compression reduces total task cost and preserves task success within an agreed noninferiority margin. Output replay alone cannot reveal changes in agent behavior.')
table(['Design element','Prespecified requirement'],[
 ['Arms','A: proxy pass-through; B: cleanup/shape without lossy condensation; C: current full pipeline. Optionally add a diff-preserving arm as a separate contrast.'],
 ['Pairing and allocation','Run the same tasks from identical repository snapshots; randomize arm order and repeat stochastic runs. Pin provider/model, CLI, settings, tools and context budget.'],
 ['Cache control','Define cold or standardized warm starts; isolate cache namespaces where supported; counterbalance order and log actual cache usage.'],
 ['Primary cost','All request and response usage through task completion or a fixed stop rule, including recovery, summaries, retries and review work included in scope.'],
 ['Primary quality','Blinded assessment against task acceptance criteria plus relevant checks. Include incomplete runs and failures in the assigned arm.'],
 ['Secondary outcomes','Wall time, tool calls, follow-up episodes, exact per-stage token/byte changes and preservation of critical evidence.']],[130,340])
p('For a task set with costs C_k,A and C_k,C, report the ratio of total costs, not the mean of task-level percentage reductions:')
equation(r'\widehat{S}_{\rm cost}=1-\frac{\sum_{k=1}^{n}C_{k,C}}{\sum_{k=1}^{n}C_{k,A}}',6)
p('Resample tasks with all paired arm runs together; stratify by provider and task family. Requests within tasks are dependent. A pilot must estimate task-level variance and disagreement rates before setting the final sample size.')
p('Let Q be task-success probability. Prespecify an acceptable margin delta and require the lower confidence bound for Q_C - Q_A to exceed -delta. Require both a positive lower bound on cost savings and this quality criterion before claiming reliable net savings without material quality loss. Report all assigned tasks; selecting successful runs alone can hide compression-induced failures.')
note('This is a proposed protocol. No randomized outcome experiment, new database logging, or paid model benchmark was initiated for this draft.')

page('13. Interpretation and reproducibility')
p('The evidence supports a precise claim: <b>TokenSaver reduced transmitted request-body bytes in the observed workload, with different rates by provider.</b> Accepted encoded replacements cannot enlarge the rewritten request under the splice contract, and the focused regression suite passes.')
p('Net token economics remain unresolved. Claude\'s gross reduction is small in this window, while related follow-ups are common among its truncated results. Codex removes more data but shows a substantial diff-related follow-up signal. These patterns motivate further comparisons; they do not establish a monetary gain or loss caused by compression.')
p('Public claims should retain dates, workload and denominators. Bytes/4 are estimated tokens, not billed usage; absent follow-ups do not prove correctness. The preservation theorem explains why quality evidence must be tied to specific tasks.')
h('Reproduction package')
table(['File','Purpose'],[
 ['tokensaver-research-draft.tex','Standalone editable paper source; inline vector figure definitions and equations.'],
 ['output/pdf/tokensaver-research-draft.pdf','Typeset reading copy generated from the same manuscript blocks.'],
 ['evidence.json / daily-series.csv','Published aggregates, daily rows, computed ratios and explicit units.'],
 ['source-manifest.json','SHA-256 hashes of source files, repository revisions and source locations.'],
 ['validation.json','Exact focused test command, result, environment and calculation assertions.'],
 ['build_paper.py / figures/','Offline paper builder; publication figures in PDF, SVG and PNG.']],[185,285])
p('The builder parses the dossier\'s daily table and September 8 aggregates, recomputes ratios and produces nine figures. research-discovery.txt records the expanded search. No application database was queried; raw captured text is excluded. LaTeX embeds its plot data, while the PDF uses the same manuscript blocks and measurements.')
note('Rebuild: run Python on build_paper.py; plotting dependencies are in .deps. Internal source paths are local provenance pointers. External publication needs a redacted, immutable aggregate archive and independently runnable benchmark.')

page('References and provenance')
refs=[
 ('1','VibeRails research archive. Does lossy tool-output compression save tokens, or just move them? A measurement dossier from 65 days of proxied coding-agent traffic. October 1, 2026. token_saver/research_2026-10-01.md; sections 3-9 and Appendix A.',''),
 ('2','VibeRails research archive. Tiered follow-up analysis and extraction code. python-scripts/token_saver/tiered_rerun_audit.py (counts and character sums), scan_conversations.py (string lengths and retained timelines), timeline_stats.py (composition and daily grouping). Saved corrected aggregate: results/vibe6-paper/tiered_rerun_2026-08-30_20261001_201359.md.',''),
 ('3','VibeRails source. TokenSaver/Minify/AnthropicMessagesRewriter.cs:188, 201, 229; CodexResponsesRewriter.cs:209; ChatCompletionsRewriter.cs:176. Encoded replacement gates, accepted traces and fallback copying. TokenSaver/Minify/OutputCondenser.cs:55, 82, 165, 263, 351. Budgets, line collapse and truncation.',''),
 ('4','VibeRails source. VibeRails.Data.Abstractions/DB/ITokenSavingsStore.cs:9-26. Per-request byte accounting and integer BytesSaved / 4 estimate. TokenSaver/Pipeline/CompressionCatalog.cs:201. Default scopes.',''),
 ('5','VibeRails tests. Tests/TokenSaver/OutputMinifierTests.cs:487; OutputCondenserTests.cs:319; PipelineGoldenFixtureTests.cs:107; ShapeFilterTests.cs:877; file-read, CRLF and provider-rewriter test classes. Focused run October 5, 2026: 475 passed, no failures or skips. Full filter in validation.json.',''),
 ('6','Anthropic. Prompt caching. Official developer documentation; accessed October 5, 2026. Used for exact-prefix requirements and distinct cache write/read prices, with model exceptions.','https://platform.claude.com/docs/en/build-with-claude/prompt-caching'),
 ('7','Anthropic. Token counting. Official developer documentation; accessed October 5, 2026. Counts depend on the selected model and may differ slightly from actual message usage.','https://platform.claude.com/docs/en/build-with-claude/token-counting'),
 ('8','VibeRails research archive. Token saver effectiveness review, October 1, 2026, with October 2 follow-up. token_saver/review_2026-10-01.md. Historical interpretation and implementation handoff; the more cautious consolidated dossier governs empirical claims in this draft.',''),
 ('9','VibeRails research archive. TokenSaver mining, September 8, 2026. python-scripts/token_saver/results/codex-mine-0908/findings.md and summary.json; saved replay-output.jsonl numeric flags. Production-source diagnostic: python-scripts/token_saver/replay_pipeline/Program.cs. Bounded request counts, replay invariants, usage buckets and candidate comparisons.',''),
 ('10','VibeRails research archive. truncate-long was cutting holes in source files: findings and fix. token_saver/truncation_file_reads.md, sections 4-6. August 29-30, 2026. Production-predicate replay and sample-specific protection coverage.',''),
 ('11','VibeRails research archive. V1 TokenSaver findings: review of Claude\'s Codex analysis. token_saver/v1_findings_claude_codex_response.md, September 4, 2026, sections 2-7. Corrected cost assumptions and ANSI-mirror estimates. Candidate estimates are not treated as realized wire savings.',''),
 ('12','VibeRails research archive. Backup coverage audit, September 14, 2026. vibe-data/docs/backup-coverage-audit.md, proxy-database and capture-limit sections. Archival Brotli ratios concern storage and are excluded from TokenSaver efficacy claims.','')]
for n,t,u in refs:
    if n=='9':page('References and provenance (continued)')
    p(f'<b>[{n}]</b> '+t)
    if u: note(u)
h('Source fingerprint')
note('Consolidated dossier SHA-256: '+hashlib.sha256(DOSSIER.read_bytes()).hexdigest())
p('The complete file-level source manifest accompanies the paper. Source line references identify the checked local revision and may move in later versions. Internal research sources are not peer-reviewed publications. Historical aggregates were reused and recalculated; the October 5 source inspection, mathematical derivations, plots and focused test run are new work for this draft.')

# Capture provenance and current verification without copying private traffic.
source_paths=[DOSSIER,BOOKS/'token_saver/review_2026-10-01.md',BOOKS/'python-scripts/token_saver/tiered_rerun_audit.py',
 BOOKS/'python-scripts/token_saver/scan_conversations.py',BOOKS/'python-scripts/token_saver/timeline_stats.py',
 PRODUCT/'TokenSaver/Minify/AnthropicMessagesRewriter.cs',PRODUCT/'TokenSaver/Minify/CodexResponsesRewriter.cs',
 PRODUCT/'TokenSaver/Minify/ChatCompletionsRewriter.cs',PRODUCT/'TokenSaver/Minify/OutputCondenser.cs',
 PRODUCT/'TokenSaver/Pipeline/CompressionCatalog.cs',PRODUCT/'VibeRails.Data.Abstractions/DB/ITokenSavingsStore.cs',
 RECENT/'findings.md',RECENT/'summary.json',RECENT/'replay-output.jsonl',BOOKS/'python-scripts/token_saver/replay_pipeline/Program.cs',
 BOOKS/'token_saver/truncation_file_reads.md',BOOKS/'token_saver/v1_findings_claude_codex_response.md',BOOKS/'vibe-data/docs/backup-coverage-audit.md']
manifest={'date':'2026-10-05','product_commit':'336373c0c51b2dc68abb3350910cee533502651a',
 'research_commit':'cf3253b99e6992e7b62564bfc3fb23f276a0e39c','files':[
 {'path':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in source_paths],
 'web_sources':[u for _,_,u in refs if u],'raw_database_recollection':False}
(ROOT/'source-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
classes=['OutputMinifierTests','OutputCondenserTests','PipelineGoldenFixtureTests','MinifierGoldenFixtureTests','ShapeFilterTests','FileReadTruncationTests','CrLfNormalizeTests','AnthropicMessagesRewriterTests','CodexResponsesRewriterTests','ChatCompletionsRewriterTests']
validation={'test_command':'dotnet test Tests/Tests.csproj --no-restore --filter "'+'|'.join('FullyQualifiedName~Tests.TokenSaver.'+x for x in classes)+'" --verbosity minimal',
 'working_directory':str(PRODUCT),'sdk':'10.0.401','framework':'net10.0','passed':475,'failed':0,'skipped':0,'exit_code':0,
 'tree_note':'Shared dirty tree; unrelated edits present, no TokenSaver or Tests/TokenSaver modifications observed before run.',
 'scope':'Finite existing tests; not task quality evidence. Three existing analyzer warnings outside selected area.',
 'calculation_checks':['65 daily rows parsed','tier counts sum to stated denominators','weighted totals calculated by sum(delta)/sum(original)','missing classifications bound 146/467 through 165/467'],
 'new_raw_traffic_measurement':False}
(ROOT/'validation.json').write_text(json.dumps(validation,indent=2),encoding='utf-8')

# Reading PDF: the same content blocks are used for the LaTeX source below.
styles=getSampleStyleSheet()
styles.add(ParagraphStyle(name='BodyPaper',fontName='Paper',fontSize=9.6,leading=13.6,textColor=colors.HexColor(NAVY),spaceAfter=7))
styles.add(ParagraphStyle(name='SectionPaper',fontName='Sans-Bold',fontSize=17,leading=22,textColor=colors.HexColor(NAVY),spaceAfter=14))
styles.add(ParagraphStyle(name='SubPaper',fontName='Sans-Bold',fontSize=11,leading=14,spaceBefore=6,spaceAfter=7,textColor=colors.HexColor(TEAL)))
styles.add(ParagraphStyle(name='NotePaper',fontName='Sans',fontSize=7.5,leading=10.4,textColor=colors.HexColor('#526779'),spaceAfter=7))
styles.add(ParagraphStyle(name='CellPaper',fontName='Sans',fontSize=8,leading=11,textColor=colors.HexColor(NAVY)))
styles.add(ParagraphStyle(name='HeadCell',fontName='Sans-Bold',fontSize=8,leading=11,textColor=colors.white))
styles.add(ParagraphStyle(name='TitlePaper',fontName='Sans-Bold',fontSize=27,leading=33,textColor=colors.HexColor(NAVY),spaceAfter=9))
styles.add(ParagraphStyle(name='SubtitlePaper',fontName='Sans',fontSize=13,leading=18,textColor=colors.HexColor(TEAL),spaceAfter=13))

inline_symbols={
 'tau_m(x)':('τ<sub>m</sub>(x)',r'\tau_m(x)'),
 'D_wire,char':('D<sub>wire,char</sub>',r'D_{\mathrm{wire,char}}'),
 'D_unique':('D<sub>unique</sub>',r'D_{\mathrm{unique}}'),
 'B_j':('B<sub>j</sub>',r'B_j'),'A_j':('A<sub>j</sub>',r'A_j'),
 's_i':('s<sub>i</sub>',r's_i'),'v_i':('v<sub>i</sub>',r'v_i'),
 'c_i':('c<sub>i</sub>',r'c_i'),'a_i':('a<sub>i</sub>',r'a_i'),
 'd_i':('d<sub>i</sub>',r'd_i'),'m_i':('m<sub>i</sub>',r'm_i'),
 'C_k,A':('C<sub>k,A</sub>',r'C_{k,A}'),'C_k,C':('C<sub>k,C</sub>',r'C_{k,C}'),
 'Q_C':('Q<sub>C</sub>',r'Q_C'),'Q_A':('Q<sub>A</sub>',r'Q_A'),
 'p_uncached':('p<sub>uncached</sub>',r'p_{\mathrm{uncached}}'),
 'p_write':('p<sub>write</sub>',r'p_{\mathrm{write}}'),
 'p_read':('p<sub>read</sub>',r'p_{\mathrm{read}}'),
 'p_output':('p<sub>output</sub>',r'p_{\mathrm{output}}')}
def pdftext(text):
    for key,(value,_) in inline_symbols.items(): text=text.replace(key,value)
    return text

def math_img(tex,num):
    fp=TMP/f'equation-{num}.png'
    fig=plt.figure(figsize=(7,.48));fig.text(.5,.5,'$'+tex+'$',ha='center',va='center',fontsize=12)
    fig.savefig(fp,dpi=260,bbox_inches='tight',pad_inches=.08);plt.close(fig)
    from PIL import Image as PILImage
    with PILImage.open(fp) as im: w,h=im.size
    return Image(str(fp),width=min(450,w/2.8),height=min(450,w/2.8)*h/w)

story=[]
for pi,(title,blocks) in enumerate(pages):
    if pi:story.append(PageBreak())
    story.append(Paragraph(html.escape(title),styles['TitlePaper' if pi==0 else 'SectionPaper']))
    for b in blocks:
        typ=b[0]
        if typ in ('p','h','note','subtitle'):
            style={'p':'BodyPaper','h':'SubPaper','note':'NotePaper','subtitle':'SubtitlePaper'}[typ]
            text=pdftext(b[1]) if typ=='p' else html.escape(b[1])
            story.append(Paragraph(text,styles[style]))
        elif typ=='eq':
            eq=math_img(b[1],b[2])
            et=Table([[eq,Paragraph(f'({b[2]})',styles['NotePaper'])]],colWidths=[445,35])
            et.setStyle(TableStyle([('VALIGN',(0,0),(-1,-1),'MIDDLE'),('LEFTPADDING',(0,0),(-1,-1),0),('RIGHTPADDING',(0,0),(-1,-1),0)]))
            story.extend([et,Spacer(1,5)])
        elif typ=='table':
            rows=[[Paragraph(html.escape(x),styles['HeadCell']) for x in b[1]]]+[[Paragraph(html.escape(str(x)),styles['CellPaper']) for x in row] for row in b[2]]
            t=Table(rows,colWidths=b[3],repeatRows=1,hAlign='LEFT')
            t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),colors.HexColor(NAVY)),('VALIGN',(0,0),(-1,-1),'TOP'),
              ('LEFTPADDING',(0,0),(-1,-1),7),('RIGHTPADDING',(0,0),(-1,-1),7),('TOPPADDING',(0,0),(-1,-1),6),('BOTTOMPADDING',(0,0),(-1,-1),6),
              ('ROWBACKGROUNDS',(0,1),(-1,-1),[colors.HexColor('#eef4f7'),colors.white]),('LINEBELOW',(0,-1),(-1,-1),.5,colors.HexColor('#dce5ea'))]))
            story.extend([t,Spacer(1,9)])
        elif typ=='fig':
            from PIL import Image as PILImage
            with PILImage.open(FIG/(b[1]+'.png')) as im:w,h=im.size
            story.append(KeepTogether([Image(str(FIG/(b[1]+'.png')),width=b[3],height=b[3]*h/w),Spacer(1,3),Paragraph(html.escape(b[2]),styles['NotePaper'])]))

def page_frame(c,doc):
    c.setStrokeColor(colors.HexColor('#d5e0e7'));c.setLineWidth(.5);c.line(54,748,558,748)
    c.setFont('Sans',7.5);c.setFillColor(colors.HexColor('#597083'));c.drawString(54,761,'VIBERAILS  /  TOKENSAVER RESEARCH')
    c.drawRightString(558,761,'DRAFT 0.2  |  2026-10-05')
    c.line(54,45,558,45);c.drawString(54,31,'Observational evidence + source audit + conditional proofs');c.drawRightString(558,31,str(doc.page))
pdf_path=OUT/'tokensaver-research-draft.pdf'
doc=SimpleDocTemplate(str(pdf_path),pagesize=(612,792),rightMargin=66,leftMargin=66,topMargin=58,bottomMargin=57,
 title='Measuring TokenSaver Compression Savings',author='Prepared for Robert Stokes',subject='Research draft: evidence, proofs and compression economics')
doc.build(story,onFirstPage=page_frame,onLaterPages=page_frame)

# Self-contained LaTeX figures. Every coordinate is embedded; no external image files are needed.
def axis(body,options=''):
    return r'\begin{tikzpicture}\begin{axis}[width=.96\linewidth,height=4.1cm,tick label style={font=\scriptsize},label style={font=\small},axis lines=left,grid=major,grid style={gray!15},'+options+']\n'+body+'\n'+r'\end{axis}\end{tikzpicture}'
texfig={}
texfig['01-observed-wire']=axis(r'\addplot+[xbar,fill=teal!75,draw=none] coordinates {(.20,3) (3.45,2) (.18,1)};','xmin=0,xmax=4,ytick={1,2,3},yticklabels={Grok CLI,Codex CLI,Claude Code},xlabel={Request-body bytes removed (\\%)},nodes near coords')
resend_panels=[]
for row in study_a:
    coords=f'(1,{row["unique_chars"]/1e6:.6f}) (2,{row["wire_chars"]/1e6:.6f})'
    plot=axis(r'\addplot+[ybar,fill=teal!70] coordinates {'+coords+'};',
      'ymin=0,ymax='+str(row['wire_chars']/1e6*1.23)+',xtick={1,2},xticklabels={Distinct,Resends},ylabel={Million characters},title={'+row['agent']+f' ({row["ratio"]:.1f}x)'+'},title style={font=\\small},bar width=16pt')
    resend_panels.append(r'\begin{minipage}{.49\linewidth}'+plot+r'\end{minipage}')
texfig['03-resend-amplification']=r'\hfill'.join(resend_panels)
texfig['04-cache-weighting']=axis(r'\addplot[gray,domain=1:40] {x};\addplot[blue,domain=1:40] {1.25+.1*(x-1)};\addplot[teal,domain=1:40] {1.25+.05*(x-1)};\addplot[orange,domain=1:40] {1.25+.025*(x-1)};\legend{Raw appearances,$\alpha=.10$,$\alpha=.05$,$\alpha=.025$}',
 'xmin=1,xmax=40,ymin=0,ymax=42,xlabel={Appearances},ylabel={Multiplier per token},legend style={font=\\tiny,at={(.02,.98)},anchor=north west}')
body=''
for key,c in [('explicit','teal!85'),('strict','blue!65'),('loose','orange!65'),('none','gray!25')]:
    coords=' '.join(f'({100*r[key]/r["n"]:.6f},{4-i})' for i,r in enumerate(tiers))
    body+=r'\addplot+[xbar,fill='+c+r',draw=none] coordinates {'+coords+'};\n'
body+=r'\legend{T0 explicit,T2 strict,T3 loose,No relation}'
texfig['05-followup-tiers']=axis(body,'xbar stacked,xmin=0,xmax=100,ytick={1,2,3,4},yticklabels={Codex grep (106),Codex diffs (284),Codex all (448),Claude (35)},xlabel={Classified instances (\\%)},legend style={font=\\tiny,at={(.5,-.35)},anchor=north,legend columns=2}')
texfig['06-break-even']=axis(r'\addplot[teal,domain=0:1] {1-x};\addplot[blue,domain=0:1] {1-2*x};\addplot[orange,domain=0:1] {1-4*x};\addplot[gray,domain=0:1] {0};\legend{$C/G=1$,$C/G=2$,$C/G=4$}',
 'xmin=0,xmax=1,ymin=-1.1,ymax=1.1,xlabel={Causally additional recovery probability $q$},ylabel={Net / gross},legend style={font=\\scriptsize,at={(.02,.02)},anchor=south west}')
panel=[]
for key,c in [('claude','teal'),('codex','blue')]:
    coords=' '.join(f'({i},{d[key]["pct"] if d[key]["pct"] is not None else "nan"})' for i,d in enumerate(daily))
    ymax=4.2 if key=='claude' else 31
    markers=''.join(r'\addplot[orange,dashed,forget plot] coordinates {('+str(i)+',0) ('+str(i)+','+str(ymax)+')};' for i in [20,31])
    if key=='claude': markers+=r'\node[font=\tiny,anchor=south] at (axis cs:20,3.6) {exec allowlisting};\node[font=\tiny,anchor=west] at (axis cs:31,3.1) {file-read protection};'
    panel.append(axis(r'\addplot['+c+r',mark=*,mark size=.65pt,unbounded coords=jump] coordinates {'+coords+'};'+markers,
      'height=3.0cm,xmin=0,xmax=64,ymin=0,ymax='+str(ymax)+',title={'+('Claude Code' if key=='claude' else 'Codex CLI')+'},title style={font=\\small},xtick={0,20,40,64},xticklabels={Jul 29,Aug 18,Sep 7,Oct 1},ylabel={Bytes removed (\\%)}'))
texfig['02-daily-series']='\n'+r'\begin{minipage}{\linewidth}\centering'+'\n'+r'\\[-1mm]'.join(panel)+'\n'+r'\end{minipage}'
recent_coords=' '.join(f'({r["pct"]:.6f},{3-i})' for i,r in enumerate(recent))
texfig['07-bounded-september-audit']=axis(r'\addplot+[xbar,fill=teal!75,draw=none] coordinates {'+recent_coords+'};',
 'xmin=0,xmax=1.7,ytick={1,2,3},yticklabels={Grok CLI,Codex CLI,Claude Code},xlabel={Recorded body bytes removed (\\%)},nodes near coords')
fp=[]
for vals,ylabel,title,maximum in [([205,56],'Output instances','Historical 261-output sample',250),([2.960996,1.095235],'Million characters','Pre-fix removal',3.7)]:
    plot=axis(r'\addplot+[ybar,fill=teal!70] coordinates {(1,'+str(vals[0])+') (2,'+str(vals[1])+')};',
      'ymin=0,ymax='+str(maximum)+',xtick={1,2},xticklabels={Detected reads,Other},ylabel={'+ylabel+'},title={'+title+'},title style={font=\\scriptsize},bar width=16pt')
    fp.append(r'\begin{minipage}{.49\linewidth}'+plot+r'\end{minipage}')
texfig['08-file-read-mitigation']=r'\hfill'.join(fp)
ap=[]
for vals,labels,title,maximum in [([actual_usage['input_tokens']/1e6,actual_usage['cache_creation_input_tokens']/1e6,actual_usage['cache_read_input_tokens']/1e6],
 'Uncached,Writes,Reads','Reported input',640),([actual_usage['cache_creation.ephemeral_5m_input_tokens']/1e6,actual_usage['cache_creation.ephemeral_1h_input_tokens']/1e6],
 '5-minute,1-hour','Cache-write buckets',12)]:
    coords=' '.join(f'({i+1},{v})' for i,v in enumerate(vals))
    plot=axis(r'\addplot+[ybar,fill=teal!70] coordinates {'+coords+'};',
      'ymin=0,ymax='+str(maximum)+',xtick={'+','.join(str(i+1) for i in range(len(vals)))+'},xticklabels={'+labels+'},ylabel={Million tokens},title={'+title+'},title style={font=\\scriptsize},bar width=13pt')
    ap.append(r'\begin{minipage}{.49\linewidth}'+plot+r'\end{minipage}')
texfig['09-actual-usage-buckets']=r'\hfill'.join(ap)

def esc(s):
    s=html.unescape(s)
    repl={'\\':r'\textbackslash{}','&':r'\&','%':r'\%','$':r'\$','#':r'\#','_':r'\_','{':r'\{','}':r'\}','~':r'\textasciitilde{}','^':r'\textasciicircum{}'}
    return ''.join(repl.get(c,c) for c in s)
def textext(s):
    pieces=re.split(r'(<b>|</b>)',s);out=''
    for x in pieces:out+=r'\textbf{' if x=='<b>' else '}' if x=='</b>' else esc(x)
    for key,(_,value) in inline_symbols.items():out=out.replace(esc(key),'$'+value+'$')
    return out
tex=[r'''\documentclass[10pt,letterpaper]{article}
\usepackage[margin=.8in]{geometry}
\usepackage[T1]{fontenc}
\usepackage{lmodern,amsmath,amssymb,booktabs,array,longtable,xcolor,graphicx,pgfplots,fancyhdr}
\usepackage[breaklinks,hidelinks]{hyperref}
\pgfplotsset{compat=1.18}
\definecolor{navy}{HTML}{17324D}
\definecolor{teal}{HTML}{087F8C}
\setlength{\parindent}{0pt}\setlength{\parskip}{6pt}
\setlength{\emergencystretch}{3em}
\pagestyle{fancy}\fancyhf{}\fancyhead[L]{\scriptsize VIBERAILS / TOKENSAVER RESEARCH}
\fancyhead[R]{\scriptsize DRAFT 0.2 / 2026-10-05}\fancyfoot[C]{\thepage}
\setlength{\headheight}{14pt}
\hypersetup{pdftitle={Measuring TokenSaver Compression Savings},pdfauthor={Prepared for Robert Stokes}}
\begin{document}
''']
for pi,(title,blocks) in enumerate(pages):
    if pi:tex.append(r'\clearpage')
    tex.append((r'{\huge\bfseries\color{navy} ' if pi==0 else r'{\Large\bfseries\color{navy} ')+esc(title)+'}\\[7pt]\n')
    for b in blocks:
        typ=b[0]
        if typ=='p':tex.append(textext(b[1])+'\n\n')
        elif typ=='h':tex.append(r'\subsection*{'+esc(b[1])+'}\n')
        elif typ=='subtitle':tex.append(r'{\large\color{teal}'+esc(b[1])+'}\\[5pt]\n')
        elif typ=='note':
            val=r'\url{'+b[1]+'}' if b[1].startswith('https://') else esc(b[1])
            tex.append(r'{\footnotesize\color{gray} '+val+'}\n\n')
        elif typ=='eq':tex.append(r'\begin{equation}'+b[1]+r'\tag{'+str(b[2])+r'}\end{equation}'+'\n')
        elif typ=='fig':tex.append(r'\begin{center}'+texfig[b[1]]+r'\end{center}'+'\n'+r'{\footnotesize '+esc(b[2])+'}\n\n')
        elif typ=='table':
            widths=b[3] or [470/len(b[1])]*len(b[1]); widths=[max(.1,w/470-.032) for w in widths]
            spec=''.join('>{\\raggedright\\arraybackslash}p{'+f'{w:.3f}'+r'\linewidth}' for w in widths)
            tex.append(r'{\small\begin{longtable}{'+spec+'}\toprule\n'+' & '.join(r'\textbf{'+esc(x)+'}' for x in b[1])+r'\\\midrule'+'\n')
            for row in b[2]:tex.append(' & '.join(esc(str(x)) for x in row)+r'\\[4pt]'+'\n')
            tex.append(r'\bottomrule\end{longtable}}'+'\n')
tex.append(r'\end{document}')
tex_path=ROOT/'tokensaver-research-draft.tex';tex_path.write_text('\n'.join(tex),encoding='utf-8')
reader=PdfReader(str(pdf_path))
page_report=[]
for i,pdf_page in enumerate(reader.pages):
    text=pdf_page.extract_text();page_report.append({'page':i+1,'characters':len(text),'opening':text[:140]})
(ROOT/'pdf-page-check.json').write_text(json.dumps(page_report,indent=2),encoding='utf-8')
print(json.dumps({'pdf':str(pdf_path),'tex':str(tex_path),'pages':len(reader.pages),'figures':9,'recent_audit':recent,'actual_usage':actual_usage,'later_totals':late},indent=2))
