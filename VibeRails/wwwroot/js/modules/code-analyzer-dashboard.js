import {
    buildMintLintReportViewModel,
    mintLintConcernTone
} from './git-guard-preflight.js';
import { ensureMonaco } from './monaco-loader.js';

const CODE_ANALYZER_SESSIONS = new WeakMap();

const MONACO_LANGUAGE_BY_EXTENSION = Object.freeze({
    '.bash': 'shell',
    '.c': 'cpp',
    '.cc': 'cpp',
    '.cpp': 'cpp',
    '.cs': 'csharp',
    '.cshtml': 'razor',
    '.css': 'css',
    '.fs': 'fsharp',
    '.go': 'go',
    '.h': 'cpp',
    '.hpp': 'cpp',
    '.html': 'html',
    '.ini': 'ini',
    '.java': 'java',
    '.js': 'javascript',
    '.json': 'json',
    '.jsx': 'javascript',
    '.kt': 'kotlin',
    '.kts': 'kotlin',
    '.less': 'less',
    '.md': 'markdown',
    '.mjs': 'javascript',
    '.php': 'php',
    '.ps1': 'powershell',
    '.py': 'python',
    '.razor': 'razor',
    '.rb': 'ruby',
    '.rs': 'rust',
    '.scss': 'scss',
    '.sh': 'shell',
    '.sql': 'sql',
    '.swift': 'swift',
    '.ts': 'typescript',
    '.tsx': 'typescript',
    '.vb': 'vb',
    '.xml': 'xml',
    '.yaml': 'yaml',
    '.yml': 'yaml'
});

const METRIC_GUIDANCE = Object.freeze({
    lines_of_code: {
        description: 'Large files are harder to review, navigate, and change safely.',
        action: 'Split the file by responsibility and keep the public entry point small.'
    },
    cyclomatic_complexity: {
        description: 'Independent control-flow paths increase the number of cases that need tests.',
        action: 'Extract decision branches into named methods or replace repeated branches with a strategy.'
    },
    cognitive_complexity: {
        description: 'Nested and interrupted control flow makes the code harder for a person to follow.',
        action: 'Use guard clauses and extract nested decisions into intention-revealing methods.'
    },
    npath_complexity: {
        description: 'This estimates how many acyclic execution paths can flow through the code.',
        action: 'Break the method into sequential stages and isolate optional behavior.'
    },
    nesting_depth: {
        description: 'Deep nesting makes state, ownership, and exit conditions difficult to track.',
        action: 'Return early for invalid states and extract nested loops or branches.'
    },
    parameter_count: {
        description: 'Long parameter lists make call sites harder to understand and easier to misuse.',
        action: 'Group related values into a focused request type or move behavior closer to its data.'
    },
    halstead_difficulty: {
        description: 'Dense combinations of operators and operands increase the effort needed to understand the code.',
        action: 'Introduce named intermediate values and split dense expressions into focused steps.'
    },
    maintainability_index: {
        description: 'The maintainability index combines size, complexity, and operator volume into one signal.',
        action: 'Reduce the strongest underlying size or complexity driver first.'
    },
    lack_of_cohesion: {
        description: 'Low cohesion suggests the type owns responsibilities that do not naturally belong together.',
        action: 'Split unrelated field and method clusters into focused collaborators.'
    },
    fan_out: {
        description: 'High fan-out means this code depends on many other components and can be costly to change.',
        action: 'Depend on narrow interfaces and introduce a facade for related infrastructure calls.'
    },
    duplication: {
        description: 'Duplicated behavior can drift and forces the same change to be made in several places.',
        action: 'Extract the shared intent after confirming the copies change for the same reason.'
    },
    hard_coded_dependencies: {
        description: 'Constructing concrete dependencies inline makes behavior harder to replace and test.',
        action: 'Inject the dependency through the constructor or a narrow factory abstraction.'
    },
    ambient_dependencies: {
        description: 'Global state and ambient services hide inputs and make behavior order-dependent.',
        action: 'Pass the dependency explicitly and keep access to global state at the application boundary.'
    },
    method_count: {
        description: 'A large method surface often signals that a type owns too many responsibilities.',
        action: 'Group related behavior and move independent responsibilities into focused types.'
    },
    field_count: {
        description: 'Many fields increase the number of states a type can occupy and the invariants it must protect.',
        action: 'Group related state into cohesive value objects and remove state that can be derived.'
    }
});

/**
 * Scores are optional on the wire: a scan that found no changed code sends
 * `healthScore: null`. Number(null) is 0 — not NaN — so the null must be rejected
 * before coercion, otherwise an empty scan reads as a hard zero (grade F,
 * "Change required") instead of "no score".
 */
function clampScore(value) {
    if (value === null || value === undefined || value === '') return null;
    const score = Number(value);
    return Number.isFinite(score) ? Math.min(100, Math.max(0, score)) : null;
}

function concernToHealth(value) {
    const concern = clampScore(value);
    return concern === null ? null : 100 - concern;
}

function formatScore(value) {
    const score = clampScore(value);
    if (score === null) return '—';
    return Number.isInteger(score) ? String(score) : score.toFixed(1);
}

function formatRating(value) {
    const rating = String(value || '').trim();
    if (!rating) return 'Not rated';
    return rating
        .replace(/([a-z])([A-Z])/g, '$1 $2')
        .replace(/[-_]+/g, ' ')
        .replace(/^./, character => character.toUpperCase());
}

function formatMetricValue(metric) {
    if (!Number.isFinite(metric?.value)) return '—';
    if (metric.name === 'duplication') return `${(metric.value * 100).toFixed(1)}%`;
    return Number.isInteger(metric.value) ? String(metric.value) : metric.value.toFixed(1);
}

function formatThreshold(value, metric) {
    if (!Number.isFinite(value)) return '—';
    if (metric?.name === 'duplication') return `${(value * 100).toFixed(1)}%`;
    return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

function formatDuration(durationMs) {
    const value = Number(durationMs);
    if (!Number.isFinite(value) || value < 0) return '';
    return value < 1000 ? `${Math.round(value)} ms` : `${(value / 1000).toFixed(value < 10000 ? 1 : 0)} s`;
}

function formatScanTime(startedUtc) {
    if (!startedUtc) return '';
    const date = new Date(startedUtc);
    if (Number.isNaN(date.valueOf())) return '';
    return date.toLocaleString(undefined, {
        month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit'
    });
}

function qualityTone(health) {
    if (!Number.isFinite(health)) return 'neutral';
    if (health >= 70) return 'success';
    if (health >= 45) return 'warning';
    return 'danger';
}

function qualityGrade(health) {
    if (!Number.isFinite(health)) return '—';
    if (health >= 90) return 'A';
    if (health >= 80) return 'B';
    if (health >= 70) return 'C';
    if (health >= 55) return 'D';
    return 'F';
}

function concernSeverity(concern) {
    const value = Number(concern);
    if (!Number.isFinite(value)) return 'unknown';
    if (value >= 75) return 'critical';
    if (value >= 55) return 'warning';
    return 'healthy';
}

/**
 * Human phrasing for a raw value's direction ('LB'/'HB'/'NA'). The concern score is
 * always higher-is-worse; this tag explains the MEASURED number next to it, so
 * "Maintainability index · 0" can say "higher is better" instead of reading as praise.
 */
function directionHint(direction) {
    if (direction === 'HB') return { arrow: '↑', text: 'higher is better' };
    if (direction === 'LB') return { arrow: '↓', text: 'lower is better' };
    return null;
}

/**
 * Fallback mirror of the backend's fixed score-card roster (MintLintReportFactory
 * .ScorecardDefinitions), used only for payloads from an older backend.
 */
const SCORECARD_ROSTER = Object.freeze([
    ['Cyclomatic complexity', ['cyclomatic_complexity']],
    ['Cognitive complexity', ['cognitive_complexity']],
    ['NPath complexity', ['npath_complexity']],
    ['Lack of cohesion (LCOM4)', ['lack_of_cohesion']],
    ['Maintainability index', ['maintainability_index']],
    ['Halstead difficulty', ['halstead_difficulty']],
    ['Coupling', ['fan_out', 'hard_coded_dependencies']],
    ['Testability', ['hard_coded_dependencies', 'ambient_dependencies']]
]);

function buildScorecardFallback(allMetrics) {
    return SCORECARD_ROSTER.map(([label, names]) => {
        // Per member metric: running totals plus its single worst measurement. The row
        // reports the member with the higher AVERAGE risk across the changeset.
        const buckets = new Map();
        for (const metric of allMetrics) {
            if (!names.includes(metric.name) || !Number.isFinite(metric.score)) continue;
            const bucket = buckets.get(metric.name) || { sumValue: 0, sumScore: 0, count: 0, worst: null };
            bucket.sumValue += Number(metric.value) || 0;
            bucket.sumScore += metric.score;
            bucket.count += 1;
            if (!bucket.worst || metric.score > bucket.worst.score) bucket.worst = metric;
            buckets.set(metric.name, bucket);
        }
        let winnerName = null;
        let winner = null;
        for (const [name, bucket] of buckets) {
            if (!winner || bucket.sumScore / bucket.count > winner.sumScore / winner.count) {
                winner = bucket;
                winnerName = name;
            }
        }
        return winner
            ? {
                label,
                measured: true,
                fileCount: winner.count,
                averageValue: winner.sumValue / winner.count,
                averageConcern: clampScore(winner.sumScore / winner.count),
                worstValue: winner.worst.value,
                worstConcern: clampScore(winner.worst.score),
                direction: winner.worst.direction,
                metricName: winnerName,
                metricLabel: winner.worst.label,
                file: winner.worst.file || '',
                source: winner.worst.source || '',
                line: winner.worst.line ?? null
            }
            : {
                label, measured: false, fileCount: 0, averageValue: null, averageConcern: null,
                worstValue: null, worstConcern: null, direction: 'NA', metricName: '', metricLabel: '',
                file: '', source: '', line: null
            };
    });
}

function splitPath(path) {
    const value = String(path || 'Unknown file');
    const lastSeparator = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
    if (lastSeparator < 0) return { folder: 'repository root', name: value, folderPath: '' };
    return {
        folder: value.slice(0, lastSeparator) || 'repository root',
        name: value.slice(lastSeparator + 1) || value,
        folderPath: value.slice(0, lastSeparator) || ''
    };
}

/**
 * True when a directory path contains a segment that names it a test tree —
 * lets the file rail badge "Tests/…" groups so test churn reads apart from
 * production churn at a glance.
 */
function isTestPath(folderPath) {
    return String(folderPath || '')
        .split(/[\\/]/)
        .some(segment => /^(tests?|testing|uitests|__tests__|specs?)$/i.test(segment));
}

/**
 * The directory path of a file, forward slashes, no trailing slash, empty string for
 * repository root. Used when ignoring "the folder of" a file — sent to the backend
 * as the path of a directory-type ignore rule.
 */
export function directoryOf(filePath) {
    const value = String(filePath || '');
    const lastSeparator = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
    return lastSeparator < 0 ? '' : value.slice(0, lastSeparator);
}

function metricKey(file, metric) {
    return `${file || ''}\u0000${metric?.name || ''}`;
}

function worstMetric(metrics) {
    return [...metrics].sort((left, right) => (Number(right.score) || 0) - (Number(left.score) || 0))[0] || null;
}

export function getMonacoLanguageForPath(path) {
    const normalized = String(path || '').trim().toLowerCase();
    const fileName = normalized.split(/[\\/]/).pop() || '';
    if (fileName === 'dockerfile' || fileName.endsWith('.dockerfile')) return 'dockerfile';
    const extensionIndex = fileName.lastIndexOf('.');
    const extension = extensionIndex >= 0 ? fileName.slice(extensionIndex) : '';
    return MONACO_LANGUAGE_BY_EXTENSION[extension] || 'plaintext';
}

/** Builds the scan-oriented view model used by the Rules-page Code Quality dashboard. */
export function buildCodeAnalyzerDashboardModel(response = {}) {
    const report = buildMintLintReportViewModel({ report: response?.report });
    const worstByMetric = new Map(
        report.worstMetrics.map(metric => [metricKey(metric.file, metric), metric]));

    const files = report.files.map(file => {
        const pathParts = splitPath(file.path);
        const categories = file.categories.map(category => ({
            ...category,
            metrics: category.metrics.map(metric => {
                const worst = worstByMetric.get(metricKey(file.path, metric));
                return {
                    ...metric,
                    category: category.name,
                    file: file.path,
                    snippet: metric.snippet || worst?.snippet || ''
                };
            })
        }));
        const metrics = categories.flatMap(category => category.metrics);
        const concern = clampScore(file.score);
        const health = concernToHealth(concern);
        return {
            ...file,
            ...pathParts,
            categories,
            metrics,
            concern,
            health,
            tone: mintLintConcernTone(concern),
            severity: concernSeverity(concern),
            qualityGrade: qualityGrade(health),
            ratingLabel: formatRating(file.rating),
            priorityMetric: worstMetric(metrics),
            // folderPath is the raw directory portion (no label fallback) so the UI
            // can pass it to the backend when ignoring "the folder of" this file.
            folderPath: pathParts.folderPath || ''
        };
    });

    const allMetrics = files.flatMap(file => file.metrics);

    // The Overview strip is backend-decided (report.overview). The client aggregation
    // below only remains as a fallback for payloads from an older backend.
    let overviewCards = (report.overview ?? [])
        .filter(card => Number.isFinite(Number(card.concern)))
        .map(card => ({
            name: card.name,
            concern: clampScore(card.concern),
            worstConcern: card.worstConcern === null ? null : clampScore(card.worstConcern),
            direction: card.direction || 'NA',
            worstLabel: card.worstMetricLabel || '',
            worstValue: card.worstMetricValue !== null && card.worstMetricName
                ? formatMetricValue({ name: card.worstMetricName, value: card.worstMetricValue })
                : null,
            worstDirection: card.worstMetricDirection || 'NA',
            worstFile: card.worstMetricFile || ''
        }));
    if (!overviewCards.length) {
        const categoryMap = new Map();
        const categoryTotals = new Map();
        for (const file of files) {
            for (const category of file.categories) {
                const total = categoryTotals.get(category.name) || { sum: 0, count: 0 };
                total.sum += Number(category.score) || 0;
                total.count += 1;
                categoryTotals.set(category.name, total);

                const existing = categoryMap.get(category.name);
                if (!existing || Number(category.score) > Number(existing.worstConcern)) {
                    const metric = worstMetric(category.metrics);
                    categoryMap.set(category.name, {
                        name: category.name,
                        worstConcern: clampScore(category.score),
                        direction: category.direction || 'NA',
                        worstLabel: metric ? metric.label : '',
                        worstValue: metric ? formatMetricValue(metric) : null,
                        worstDirection: metric ? metric.direction : 'NA',
                        worstFile: file.path
                    });
                }
            }
        }
        overviewCards = [...categoryMap.values()].map(card => {
            const total = categoryTotals.get(card.name);
            return { ...card, concern: clampScore(total.sum / total.count) };
        });
    }

    const responseHealth = clampScore(response?.healthScore);
    const reportConcern = clampScore(response?.report?.score);
    const health = responseHealth ?? concernToHealth(reportConcern);
    const criticalCount = allMetrics.filter(metric => concernSeverity(metric.score) === 'critical').length;
    const warningCount = allMetrics.filter(metric => concernSeverity(metric.score) === 'warning').length;
    const healthyFileCount = files.filter(file => file.severity === 'healthy').length;
    const analyzedFileCount = Math.max(0, Number.parseInt(response?.analyzedFileCount, 10) || files.length);
    const skippedFileCount = Math.max(0, Number.parseInt(response?.skippedFileCount, 10) || 0);
    const rating = formatRating(response?.rating || response?.report?.rating);

    return {
        health,
        healthLabel: health === null ? 'No changed code' : health >= 85 ? 'Healthy change' : health >= 70 ? 'Review ready' : health >= 45 ? 'Needs attention' : 'Change required',
        rating,
        qualityGrade: qualityGrade(health),
        tone: qualityTone(health),
        analyzedFileCount,
        skippedFileCount,
        ignoredFileCount: Math.max(0, Number.parseInt(response?.ignoredFileCount, 10) || 0),
        criticalCount,
        warningCount,
        healthyFileCount,
        categories: overviewCards,
        scorecard: report.scorecard?.length ? report.scorecard : buildScorecardFallback(allMetrics),
        files,
        scannedAt: formatScanTime(response?.startedUtc),
        duration: formatDuration(response?.durationMs)
    };
}

function element(documentRef, tagName, className = '', text = '') {
    const node = documentRef.createElement(tagName);
    if (className) node.className = className;
    if (text !== '') node.textContent = String(text);
    return node;
}

function icon(documentRef, className) {
    const node = element(documentRef, 'i', className);
    node.setAttribute('aria-hidden', 'true');
    return node;
}

function setTone(node, tone) {
    if (node?.dataset) node.dataset.tone = tone;
}

function setStyleProperty(node, name, value) {
    node?.style?.setProperty?.(name, value);
}

function appendChip(documentRef, host, label, value) {
    const chip = element(documentRef, 'span', 'code-analyzer-chip');
    chip.append(element(documentRef, 'span', '', label), element(documentRef, 'strong', '', value));
    host.append(chip);
}

/**
 * Renders the compact Code quality summary shown on the Validation screen — the
 * survivor of the old Overview tab. One row: score ring, grade, verdict, counts,
 * and the top risk categories. Pass a null response to clear and hide the host.
 */
export function renderCodeAnalyzerBrief(host, response, documentRef = globalThis.document, options = {}) {
    if (!host || !documentRef?.createElement) return false;
    if (!response) {
        host.replaceChildren?.();
        host.hidden = true;
        return false;
    }

    const model = buildCodeAnalyzerDashboardModel(response);
    const onOpenDetails = typeof options.onOpenDetails === 'function' ? options.onOpenDetails : null;

    const brief = element(documentRef, 'article', 'code-analyzer-brief');
    setTone(brief, model.tone);

    const ring = element(documentRef, 'div', 'code-analyzer-brief-ring');
    setStyleProperty(ring, '--score', String(model.health || 0));
    const ringCenter = element(documentRef, 'div', 'code-analyzer-brief-ring-center');
    ringCenter.append(element(documentRef, 'strong', '', formatScore(model.health)));
    ringCenter.title = 'Quality score out of 100 — higher is healthier';
    ring.append(ringCenter);
    brief.append(ring);

    const copy = element(documentRef, 'div', 'code-analyzer-brief-copy');
    const headline = element(documentRef, 'div', 'code-analyzer-brief-headline');
    const grade = element(documentRef, 'span', 'code-analyzer-rating-badge', model.qualityGrade);
    setTone(grade, model.tone);
    headline.append(element(documentRef, 'span', 'code-analyzer-eyebrow', 'Code quality'), grade);
    const summaryBits = [
        `${model.analyzedFileCount} file${model.analyzedFileCount === 1 ? '' : 's'} analyzed`,
        model.scannedAt ? `scanned ${model.scannedAt}` : ''
    ].filter(Boolean);
    copy.append(
        headline,
        element(documentRef, 'h3', '', model.healthLabel),
        element(documentRef, 'p', '', summaryBits.join(' · ')));
    brief.append(copy);

    const stats = element(documentRef, 'div', 'code-analyzer-brief-stats');
    const statDefinitions = [
        [model.criticalCount, 'critical', 'danger'],
        [model.warningCount, model.warningCount === 1 ? 'warning' : 'warnings', 'warning'],
        [model.healthyFileCount, 'healthy', 'success']
    ];
    if (model.ignoredFileCount > 0) statDefinitions.push([model.ignoredFileCount, 'ignored', 'neutral']);
    for (const [value, label, tone] of statDefinitions) {
        const stat = element(documentRef, 'span', 'code-analyzer-brief-stat');
        setTone(stat, value > 0 ? tone : 'neutral');
        stat.append(element(documentRef, 'strong', '', value), element(documentRef, 'span', '', label));
        stats.append(stat);
    }
    brief.append(stats);

    // The three riskiest categories stand in for the old category grid.
    const topCategories = [...model.categories]
        .filter(category => Number.isFinite(category.concern))
        .sort((left, right) => right.concern - left.concern)
        .slice(0, 3);
    if (topCategories.length) {
        const categories = element(documentRef, 'div', 'code-analyzer-brief-categories');
        for (const category of topCategories) {
            const chip = element(documentRef, 'span', 'code-analyzer-brief-category');
            setTone(chip, mintLintConcernTone(category.concern));
            if (category.worstLabel) {
                chip.title = `Worst signal: ${category.worstLabel}${category.worstFile ? ` in ${category.worstFile}` : ''}`;
            }
            chip.append(
                element(documentRef, 'i'),
                element(documentRef, 'span', '', category.name),
                element(documentRef, 'strong', '', formatScore(category.concern)));
            categories.append(chip);
        }
        brief.append(categories);
    }

    if (onOpenDetails) {
        const open = element(documentRef, 'button', 'code-analyzer-brief-open');
        open.type = 'button';
        open.title = 'Open the file-by-file report';
        open.append(
            element(documentRef, 'span', '', 'Open full report'),
            icon(documentRef, 'fa-solid fa-arrow-right'));
        open.addEventListener?.('click', () => onOpenDetails());
        brief.append(open);
    }

    host.replaceChildren?.(brief);
    host.hidden = false;
    return true;
}

/** Human label for an ignore reason ({reasonKind, reasonText}). */
function ignoreReasonLabel(entry) {
    if (entry.reasonKind === 'test') return 'Test files';
    if (entry.reasonKind === 'config') return 'Config file';
    if (entry.reasonKind === 'other') return entry.reasonText || 'Other';
    return entry.reasonText || '';
}

function renderFileOverview(documentRef, file, onIgnoreFile, onIgnoreDirectory) {
    const overview = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-file-overview');
    const titleRow = element(documentRef, 'div', 'code-analyzer-file-title-row');
    const titleWrap = element(documentRef, 'div', 'code-analyzer-file-title-wrap');
    const kicker = element(documentRef, 'span', 'code-analyzer-file-kicker', file.folder);
    kicker.title = file.path;
    titleWrap.append(
        kicker,
        element(documentRef, 'h3', 'code-analyzer-file-title', file.name));
    const meta = element(documentRef, 'div', 'code-analyzer-inline-meta');
    appendChip(documentRef, meta, 'Used by', `${file.referencedBy} file${file.referencedBy === 1 ? '' : 's'}`);
    if (Number.isFinite(file.priority)) appendChip(documentRef, meta, 'Priority', file.priority.toFixed(1));
    if (Number.isFinite(file.baseline)) appendChip(documentRef, meta, 'Baseline risk', file.baseline.toFixed(1));
    if (Number.isFinite(file.introduced)) {
        const introduced = file.introduced > 0 ? `+${file.introduced.toFixed(1)}` : file.introduced.toFixed(1);
        appendChip(documentRef, meta, 'This change', introduced);
    }
    titleWrap.append(meta);

    const gradeBlock = element(documentRef, 'div', 'code-analyzer-file-grade-block');
    const gradeCopy = element(documentRef, 'span', 'code-analyzer-file-grade-copy');
    gradeCopy.append(
        element(documentRef, 'strong', '', `${formatScore(file.health)} / 100`),
        element(documentRef, 'small', '', 'file quality score'));
    const rating = element(documentRef, 'span', 'code-analyzer-large-rating', file.qualityGrade);
    setTone(rating, qualityTone(file.health));
    gradeBlock.append(gradeCopy, rating);
    if (typeof onIgnoreFile === 'function') {
        const ignoreButton = element(documentRef, 'button', 'code-analyzer-ignore-button');
        ignoreButton.type = 'button';
        ignoreButton.title = 'Ignore this file — removes it from Code quality results';
        ignoreButton.append(icon(documentRef, 'fa-solid fa-eye-slash'), element(documentRef, 'span', '', 'Ignore'));
        ignoreButton.addEventListener?.('click', () => onIgnoreFile(file));
        gradeBlock.append(ignoreButton);
    }
    // "Ignore folder" drops the whole directory containing this file. Disabled when
    // the file lives at the repo root (no folder to ignore without nuking everything).
    if (typeof onIgnoreDirectory === 'function' && file.folderPath) {
        const folderButton = element(documentRef, 'button', 'code-analyzer-ignore-button code-analyzer-ignore-folder-button');
        folderButton.type = 'button';
        folderButton.title = `Ignore everything in ${file.folderPath}/ — removes the whole directory from Code quality results`;
        folderButton.append(icon(documentRef, 'fa-solid fa-folder-tree'), element(documentRef, 'span', '', 'Ignore folder'));
        folderButton.addEventListener?.('click', () => onIgnoreDirectory({ file }));
        gradeBlock.append(folderButton);
    }
    titleRow.append(titleWrap, gradeBlock);
    overview.append(titleRow);

    if (file.priorityMetric) {
        const finding = element(documentRef, 'div', 'code-analyzer-priority-finding');
        setTone(finding, mintLintConcernTone(file.priorityMetric.score));
        const mark = element(documentRef, 'span', 'code-analyzer-priority-icon');
        mark.append(icon(documentRef, 'fa-solid fa-triangle-exclamation'));
        const findingCopy = element(documentRef, 'span', 'code-analyzer-priority-copy');
        findingCopy.append(
            element(documentRef, 'strong', '', `${file.priorityMetric.label} is the leading concern`),
            element(documentRef, 'small', '', `${formatMetricValue(file.priorityMetric)} measured · risk ${formatScore(file.priorityMetric.score)}`));
        const location = element(documentRef, 'span', 'code-analyzer-priority-location');
        location.append(
            element(documentRef, 'strong', '', file.priorityMetric.source || file.name),
            element(documentRef, 'small', '', file.priorityMetric.line ? `Line ${file.priorityMetric.line}` : file.ratingLabel));
        finding.append(mark, findingCopy, location);
        overview.append(finding);
    }
    return overview;
}

/**
 * The header strip of the source pane: what metric the highlighted code belongs to,
 * how bad it is, the measured value with its direction, and what to do about it.
 * Rebuilt in place when the selected metric changes within the same file.
 */
function renderSourceHeader(documentRef, file, metric) {
    const header = element(documentRef, 'header', 'code-analyzer-code-head');
    const copy = element(documentRef, 'div', 'code-analyzer-code-copy');
    if (metric) {
        copy.append(
            element(documentRef, 'span', 'code-analyzer-eyebrow', metric.category || 'Metric'),
            element(documentRef, 'h3', '', metric.label),
            element(documentRef, 'p', '', METRIC_GUIDANCE[metric.name]?.action
                || 'Review the measured location and reduce the strongest contributor first.'));
    } else {
        copy.append(
            element(documentRef, 'span', 'code-analyzer-eyebrow', file.folder),
            element(documentRef, 'h3', '', file.name),
            element(documentRef, 'p', '', 'Select a metric to jump to the code behind it.'));
    }
    header.append(copy);

    const facts = element(documentRef, 'div', 'code-analyzer-source-facts');
    if (metric) {
        const severity = element(documentRef, 'span', 'code-analyzer-severity-badge', concernSeverity(metric.score));
        setTone(severity, mintLintConcernTone(metric.score));
        facts.append(severity);

        const measured = element(documentRef, 'span', 'code-analyzer-source-fact');
        measured.append(
            element(documentRef, 'small', '', 'Measured'),
            element(documentRef, 'strong', '', formatMetricValue(metric)));
        const hint = directionHint(metric.direction);
        if (hint) {
            measured.append(element(documentRef, 'span', 'code-analyzer-direction-tag', `${hint.arrow} ${hint.text}`));
        }
        facts.append(measured);

        const thresholds = element(documentRef, 'span', 'code-analyzer-source-fact');
        thresholds.append(
            element(documentRef, 'small', '', 'Thresholds'),
            element(documentRef, 'strong', '', `W ${formatThreshold(metric.warn, metric)} · C ${formatThreshold(metric.critical, metric)}`));
        facts.append(thresholds);
    }
    const location = element(documentRef, 'span', 'code-analyzer-evidence-location');
    location.textContent = [metric?.source || file.name, metric?.line ? `L${metric.line}` : ''].filter(Boolean).join(' · ');
    facts.append(location);
    header.append(facts);
    return header;
}

/** The source pane: header strip + Monaco shell + status bar for the selected file/metric. */
function renderSourcePane(documentRef, file, metric) {
    const panel = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-code-panel');
    const header = renderSourceHeader(documentRef, file, metric);
    panel.append(header);

    const editorShell = element(documentRef, 'div', 'code-analyzer-monaco-shell');
    setTone(editorShell, mintLintConcernTone(metric ? metric.score : undefined));
    const host = element(documentRef, 'div', 'code-analyzer-monaco-host');
    host.setAttribute('data-code-analyzer-monaco-host', '');
    const loading = element(documentRef, 'div', 'code-analyzer-monaco-loading');
    const spinner = element(documentRef, 'span', 'spinner-border spinner-border-sm');
    spinner.setAttribute('aria-hidden', 'true');
    loading.append(spinner, element(documentRef, 'span', '', 'Loading source…'));
    const fallback = element(documentRef, 'pre', 'code-analyzer-code-evidence');
    fallback.textContent = metric?.snippet || `${file.path}\n\nNo source excerpt is available for this selection.`;
    fallback.hidden = true;
    editorShell.append(host, loading, fallback);

    const status = element(documentRef, 'footer', 'code-analyzer-editor-status');
    const statusLeft = element(documentRef, 'span');
    const position = element(documentRef, 'span', 'code-analyzer-editor-position', metric?.line ? `Line ${metric.line}` : file.path);
    statusLeft.append(
        element(documentRef, 'span', 'code-analyzer-editor-readonly', 'Read only'),
        position);
    const language = getMonacoLanguageForPath(file.path);
    const statusRight = element(documentRef, 'span');
    statusRight.append(
        element(documentRef, 'span', '', 'UTF-8'),
        element(documentRef, 'span', '', language));
    status.append(statusLeft, statusRight);
    panel.append(editorShell, status);
    return { panel, header, host, loading, fallback, position };
}

function disposeEvidenceEditor(session) {
    if (!session) return;
    session.generation += 1;
    // editor.dispose() also disposes the model it created, so the model is never disposed
    // here — doing so would be a double-dispose that only the try/catch was hiding.
    try { session.decorations?.clear?.(); } catch (_) { /* no-op */ }
    try { session.editor?.dispose?.(); } catch (_) { /* no-op */ }
    session.decorations = null;
    session.editor = null;
    session.model = null;
    session.editorFilePath = null;
    session.editorHasFullSource = false;
}

export function disposeCodeAnalyzerDashboard(container) {
    const session = container ? CODE_ANALYZER_SESSIONS.get(container) : null;
    if (!session) return false;
    session.disposed = true;
    disposeEvidenceEditor(session);
    try { session.disposeRail?.(); } catch (_) { /* no-op */ }
    session.disposeRail = null;
    CODE_ANALYZER_SESSIONS.delete(container);
    return true;
}

function showEvidenceFallback(view) {
    if (view.loading) view.loading.hidden = true;
    if (view.host) view.host.hidden = true;
    if (view.fallback) view.fallback.hidden = false;
}

function evidenceDecorationOptions(monaco, tone) {
    const suffix = ['danger', 'warning', 'success'].includes(tone) ? tone : 'okay';
    // Monaco's ruler/minimap APIs need literal colors; keep these in sync with the
    // Focus Dark status palette in style.css (--color-danger/-warning/-success/-accent).
    const colors = {
        danger: '#ef4444',
        warning: '#f59e0b',
        success: '#10b981',
        // Legacy fallback suffix — same green as success so a healthy marker is
        // never blue (matches mintLintConcernTone folding "okay" into success).
        okay: '#10b981'
    };
    return {
        isWholeLine: true,
        className: `mintlint-monaco-line mintlint-monaco-line-${suffix}`,
        glyphMarginClassName: `mintlint-monaco-glyph mintlint-monaco-glyph-${suffix}`,
        overviewRuler: {
            color: colors[suffix],
            position: monaco.editor.OverviewRulerLane?.Full ?? 7
        },
        minimap: {
            color: colors[suffix],
            position: monaco.editor.MinimapPosition?.Inline ?? 1
        }
    };
}

export function createCodeEvidenceEditor(monaco, host, file, metric, fullSource = null) {
    const safeMetric = metric || { name: '', label: 'source', score: undefined, line: null, snippet: '' };
    const hasFullSource = typeof fullSource === 'string' && fullSource.length > 0;
    const source = hasFullSource
        ? fullSource
        : (safeMetric.snippet || `${file.path}\n\nNo code excerpt was returned for ${safeMetric.label}.`);
    const language = getMonacoLanguageForPath(file.path);
    const metricLine = Number.isFinite(safeMetric.line) && safeMetric.line > 0 ? safeMetric.line : null;
    // With the whole file loaded, line numbers are natural and the metric line is the
    // marker. With only a snippet, the excerpt STARTS at the metric line, so numbering
    // is offset and line 1 carries the marker.
    const firstSourceLine = hasFullSource ? 1 : (metricLine ?? 1);
    const totalLines = source.split('\n').length;
    const editor = monaco.editor.create(host, {
        value: source,
        language,
        theme: 'viberails-dark',
        ariaLabel: `Read-only source for ${safeMetric.label} in ${file.path}`,
        readOnly: true,
        domReadOnly: true,
        automaticLayout: true,
        glyphMargin: true,
        folding: hasFullSource,
        lineNumbers: firstSourceLine === 1 ? 'on' : (lineNumber => String(firstSourceLine + lineNumber - 1)),
        lineNumbersMinChars: String(firstSourceLine + totalLines).length + 1,
        minimap: { enabled: totalLines > 18, showSlider: 'mouseover' },
        overviewRulerBorder: false,
        renderLineHighlight: 'all',
        renderWhitespace: 'selection',
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        wordWrap: 'off',
        fontSize: 13,
        lineHeight: 21,
        fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
        padding: { top: 12, bottom: 12 },
        contextmenu: true,
        links: false,
        occurrencesHighlight: 'off',
        selectionHighlight: false,
        stickyScroll: { enabled: false }
    });
    const tone = mintLintConcernTone(safeMetric.score);
    const markerLine = hasFullSource ? metricLine : 1;
    let decorations = null;
    if (markerLine) {
        const range = new monaco.Range(markerLine, 1, markerLine, 1);
        decorations = editor.createDecorationsCollection?.([{
            range,
            options: evidenceDecorationOptions(monaco, tone)
        }]) || null;
        editor.setSelection?.(range);
    }
    // A metric with no line (NA/file-level) starts at the top of the file.
    editor.revealLineNearTop?.(markerLine ?? 1);
    return { editor, model: editor.getModel?.() || null, decorations };
}

async function mountCodeEvidenceEditor(view, file, metric, session, fullSource = null) {
    const generation = ++session.generation;
    let monaco;
    try {
        monaco = await ensureMonaco();
    } catch (error) {
        console.error('MintLint Monaco viewer failed to load:', error);
        monaco = null;
    }
    if (session.disposed || generation !== session.generation) return;
    if (!monaco) {
        showEvidenceFallback(view);
        return;
    }

    let mounted;
    try {
        mounted = createCodeEvidenceEditor(monaco, view.host, file, metric, fullSource);
    } catch (error) {
        console.error('MintLint Monaco viewer could not create the editor:', error);
        showEvidenceFallback(view);
        return;
    }
    if (session.disposed || generation !== session.generation) {
        // Superseded while Monaco was loading; editor.dispose() takes its model with it.
        mounted.editor?.dispose?.();
        return;
    }

    session.monaco = monaco;
    session.editor = mounted.editor;
    session.model = mounted.model;
    session.decorations = mounted.decorations;
    session.editorFilePath = file.path;
    session.editorHasFullSource = typeof fullSource === 'string' && fullSource.length > 0;
    if (view.loading) view.loading.hidden = true;
    if (view.host) view.host.hidden = false;

    globalThis.requestAnimationFrame?.(() => {
        if (!session.disposed && generation === session.generation) mounted.editor.layout?.();
    });
}

function renderMetricsPanel(documentRef, file, selectedMetric, onSelect) {
    const panel = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-metrics-panel');
    const header = element(documentRef, 'header', 'code-analyzer-metrics-head');
    const copy = element(documentRef, 'div');
    copy.append(
        element(documentRef, 'h3', '', 'Health metrics'),
        element(documentRef, 'p', '', 'Select a metric to inspect its threshold, source location, and remediation direction.'));
    const legend = element(documentRef, 'span', 'code-analyzer-metrics-legend');
    for (const [tone, text] of [['success', '0–54 healthy'], ['warning', '55–74 warning'], ['danger', '75–100 critical']]) {
        const item = element(documentRef, 'span');
        const dot = element(documentRef, 'i');
        setTone(dot, tone);
        item.append(dot, element(documentRef, 'span', '', text));
        legend.append(item);
    }
    header.append(copy, legend);
    panel.append(header);

    const groups = element(documentRef, 'div', 'code-analyzer-metric-groups');
    for (const category of file.categories) {
        const group = element(documentRef, 'section', 'code-analyzer-metric-group');
        const groupHead = element(documentRef, 'div', 'code-analyzer-metric-group-head');
        groupHead.append(
            element(documentRef, 'strong', '', category.name),
            element(documentRef, 'span', '', `${formatScore(category.score)} category risk`));
        group.append(groupHead);
        const list = element(documentRef, 'div', 'code-analyzer-metric-list-dashboard');
        for (const metric of category.metrics) {
            const row = element(documentRef, 'button', 'code-analyzer-metric-row');
            row.type = 'button';
            row.setAttribute('aria-pressed', String(metric === selectedMetric));
            if (metric === selectedMetric) row.classList.add('active');
            setTone(row, mintLintConcernTone(metric.score));
            const title = element(documentRef, 'span', 'code-analyzer-metric-title');
            title.append(element(documentRef, 'i'), element(documentRef, 'span', '', metric.label));
            const concern = element(documentRef, 'span', 'code-analyzer-metric-concern');
            concern.append(element(documentRef, 'small', '', 'risk'), element(documentRef, 'strong', '', formatScore(metric.score)));
            const meter = element(documentRef, 'span', 'code-analyzer-risk-track');
            const fill = element(documentRef, 'span');
            setStyleProperty(fill, '--risk', `${clampScore(metric.score) || 0}%`);
            meter.append(fill);
            const threshold = element(documentRef, 'span', 'code-analyzer-metric-threshold');
            threshold.append(
                element(documentRef, 'strong', '', concernSeverity(metric.score)),
                element(documentRef, 'small', '', `W ${formatThreshold(metric.warn, metric)} · C ${formatThreshold(metric.critical, metric)}`));
            const raw = element(documentRef, 'span', 'code-analyzer-metric-raw', formatMetricValue(metric));
            const rowHint = directionHint(metric.direction);
            if (rowHint) {
                raw.title = `Raw value: ${rowHint.text}`;
                raw.append(element(documentRef, 'i', 'code-analyzer-direction-glyph', rowHint.arrow));
            }
            row.append(title, raw, concern, meter, threshold);
            row.addEventListener?.('click', () => onSelect(metric));
            list.append(row);
        }
        group.append(list);
        groups.append(group);
    }
    panel.append(groups);
    return panel;
}

/**
 * Builds the "Ignored files (N)" <details> box (the only restore UI), or null when nothing is
 * ignored. Extracted so it can render both inside the changed-files rail and, when every changed
 * file is ignored (an otherwise-empty report), directly into the dashboard container.
 */
function renderIgnoredFilesBox(documentRef, ignoredFiles, onRestoreFile) {
    if (!Array.isArray(ignoredFiles) || ignoredFiles.length === 0) return null;
    const ignoredBox = element(documentRef, 'details', 'code-analyzer-ignored-box');
    const summary = element(documentRef, 'summary');
    summary.append(
        icon(documentRef, 'fa-solid fa-eye-slash'),
        element(documentRef, 'span', '', `Ignored files (${ignoredFiles.length})`));
    ignoredBox.append(summary);
    const ignoredList = element(documentRef, 'div', 'code-analyzer-ignored-list');
    for (const entry of ignoredFiles) {
        const row = element(documentRef, 'div', 'code-analyzer-ignored-row');
        const copy = element(documentRef, 'span', 'code-analyzer-ignored-copy');
        copy.title = entry.path;
        copy.append(element(documentRef, 'strong', '', entry.path));
        const reason = ignoreReasonLabel(entry);
        if (reason) copy.append(element(documentRef, 'small', '', reason));
        row.append(copy);
        if (typeof onRestoreFile === 'function') {
            const restore = element(documentRef, 'button', 'code-analyzer-ignored-restore', 'Restore');
            restore.type = 'button';
            restore.title = `Scan ${entry.path} again`;
            restore.addEventListener?.('click', () => onRestoreFile(entry));
            row.append(restore);
        }
        ignoredList.append(row);
    }
    ignoredBox.append(ignoredList);
    return ignoredBox;
}

function renderFileRail(documentRef, model, selectedFile, onSelect, ignoredFiles = [], options = {}) {
    const {
        onRestoreFile = null,
        onIgnoreFile = null,
        onIgnoreDirectories = null
    } = options || {};

    const rail = element(documentRef, 'aside', 'code-analyzer-panel-surface code-analyzer-file-rail');
    const head = element(documentRef, 'header', 'code-analyzer-rail-head');
    const title = element(documentRef, 'div', 'code-analyzer-rail-title');
    title.append(
        element(documentRef, 'h3', '', 'Changed files'),
        element(documentRef, 'span', '', `${model.files.length} file${model.files.length === 1 ? '' : 's'}`));
    head.append(title);
    const search = element(documentRef, 'label', 'code-analyzer-search-box');
    search.append(icon(documentRef, 'fa-solid fa-magnifying-glass'));
    const input = element(documentRef, 'input');
    input.type = 'search';
    input.placeholder = 'Filter by file or folder';
    input.setAttribute('aria-label', 'Filter analyzed files');
    search.append(input);
    head.append(search);
    const filters = element(documentRef, 'div', 'code-analyzer-filter-row');
    filters.setAttribute('role', 'group');
    filters.setAttribute('aria-label', 'Filter file severity');
    const filterDefinitions = [['all', 'All'], ['critical', 'Critical'], ['warning', 'Warning'], ['healthy', 'Healthy']];
    const filterButtons = [];
    for (const [value, label] of filterDefinitions) {
        const button = element(documentRef, 'button', 'code-analyzer-filter-button', label);
        button.type = 'button';
        button.dataset.filter = value;
        button.classList.toggle('active', value === 'all');
        button.setAttribute('aria-pressed', String(value === 'all'));
        filters.append(button);
        filterButtons.push(button);
    }
    head.append(filters);
    rail.append(head);

    const list = element(documentRef, 'div', 'code-analyzer-file-list');
    rail.append(list);

    let activeFilter = 'all';
    // `selectedFile` is a parameter bound once at call time; the dashboard's selectFile reassigns a
    // DIFFERENT outer binding paintList never sees. Track the open file in a local the rail owns so
    // the active-row highlight follows the selection on refresh.
    let activeFile = selectedFile;
    // Directories the user collapsed, keyed by folderPath. Lives in the rail's
    // closure so it survives filter/search repaints.
    const collapsedDirs = new Set();

    // One floating context menu for the whole rail (same pattern as the chat
    // history sidebar): each row's kebab opens it anchored to that row. It hangs
    // off the rail — not the row — so the list's scroll clipping cannot cut it off.
    const contextMenu = element(documentRef, 'div', 'code-analyzer-context-menu');
    contextMenu.setAttribute('role', 'menu');
    let contextMenuAnchor = null;

    const closeContextMenu = () => {
        contextMenuAnchor = null;
        contextMenu.classList.remove?.('show');
        contextMenu.replaceChildren?.();
    };

    const positionContextMenu = (anchor) => {
        // Real DOM only — layout math is meaningless under the test fakes.
        if (!anchor.getBoundingClientRect || !rail.getBoundingClientRect) return;
        const anchorRect = anchor.getBoundingClientRect();
        const railRect = rail.getBoundingClientRect();
        const menuRect = contextMenu.getBoundingClientRect();
        // Below the anchor by default; flip above when that would leave the rail.
        let top = anchorRect.bottom - railRect.top + 4;
        if (top + menuRect.height > railRect.height - 6) {
            top = Math.max(6, anchorRect.top - railRect.top - menuRect.height - 4);
        }
        contextMenu.style.top = `${top}px`;
        contextMenu.style.right = `${Math.max(6, railRect.right - anchorRect.right)}px`;
    };

    const openContextMenu = (anchor, entries) => {
        contextMenuAnchor = anchor;
        contextMenu.replaceChildren?.();
        for (const entry of entries) {
            const menuItem = element(documentRef, 'button', 'code-analyzer-context-menu-item');
            menuItem.type = 'button';
            menuItem.setAttribute('role', 'menuitem');
            menuItem.append(icon(documentRef, entry.icon), element(documentRef, 'span', '', entry.label));
            menuItem.addEventListener?.('click', () => {
                closeContextMenu();
                entry.onPick();
            });
            contextMenu.append(menuItem);
        }
        contextMenu.classList.add('show');
        positionContextMenu(anchor);
    };

    const buildKebab = (label, entries) => {
        const kebab = element(documentRef, 'button', 'code-analyzer-kebab');
        kebab.type = 'button';
        kebab.title = label;
        kebab.setAttribute('aria-label', label);
        kebab.setAttribute('aria-haspopup', 'menu');
        kebab.append(icon(documentRef, 'fa-solid fa-ellipsis-vertical'));
        kebab.addEventListener?.('click', event => {
            event.stopPropagation?.();
            if (contextMenuAnchor === kebab) closeContextMenu();
            else openContextMenu(kebab, entries);
        });
        return kebab;
    };

    const onDocumentClick = (event) => {
        if (event.target?.closest?.('.code-analyzer-kebab, .code-analyzer-context-menu')) return;
        closeContextMenu();
    };
    documentRef.addEventListener?.('click', onDocumentClick);
    list.addEventListener?.('scroll', closeContextMenu);

    const paintList = () => {
        closeContextMenu();
        const query = String(input.value || '').trim().toLowerCase();
        const visible = model.files.filter(file =>
            (activeFilter === 'all' || file.severity === activeFilter) &&
            (!query || file.path.toLowerCase().includes(query)));
        list.replaceChildren();
        if (!visible.length) {
            list.append(element(documentRef, 'p', 'code-analyzer-file-list-empty', 'No files match this filter.'));
            return;
        }

        // Group the visible files by directory, in first-appearance order, so the
        // backend's risk-first ordering still decides what surfaces first and the
        // group header itself answers "which of these are test files?".
        const groups = new Map();
        for (const file of visible) {
            const key = file.folderPath || '';
            const group = groups.get(key);
            if (group) group.push(file);
            else groups.set(key, [file]);
        }

        for (const [folderPath, groupFiles] of groups) {
            const collapsed = collapsedDirs.has(folderPath);
            const header = element(documentRef, 'div', 'code-analyzer-dir-head');
            header.title = folderPath || 'repository root';
            header.setAttribute('role', 'button');
            header.setAttribute('aria-expanded', String(!collapsed));
            header.tabIndex = 0;

            header.append(icon(documentRef,
                `fa-solid ${collapsed ? 'fa-chevron-right' : 'fa-chevron-down'} code-analyzer-dir-chevron`));
            const pathLabel = element(documentRef, 'span', 'code-analyzer-dir-path');
            pathLabel.append(
                icon(documentRef, 'fa-regular fa-folder-open'),
                element(documentRef, 'span', '', folderPath || 'repository root'));
            header.append(pathLabel);
            if (isTestPath(folderPath)) {
                header.append(element(documentRef, 'span', 'code-analyzer-dir-tag', 'tests'));
            }
            header.append(element(documentRef, 'span', 'code-analyzer-dir-count', groupFiles.length));
            // No "Ignore directory" for the repository root — that would nuke the scan.
            if (folderPath && typeof onIgnoreDirectories === 'function') {
                header.append(buildKebab(`Actions for ${folderPath}`, [{
                    icon: 'fa-solid fa-folder-tree',
                    label: 'Ignore directory',
                    onPick: () => onIgnoreDirectories([folderPath])
                }]));
            }

            const toggleGroup = () => {
                if (collapsedDirs.has(folderPath)) collapsedDirs.delete(folderPath);
                else collapsedDirs.add(folderPath);
                paintList();
            };
            header.addEventListener?.('click', event => {
                // The kebab (and the menu it opens) must not toggle the group.
                if (event.target?.closest?.('.code-analyzer-kebab, .code-analyzer-context-menu')) return;
                toggleGroup();
            });
            header.addEventListener?.('keydown', event => {
                if (event.target !== event.currentTarget) return;
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault?.();
                    toggleGroup();
                }
            });
            list.append(header);

            if (collapsed) continue;

            for (const file of groupFiles) {
                // The rail item is a div (not a button) so we can nest the real kebab
                // button inside it — interactive content inside a <button> is invalid
                // HTML and swallows clicks inconsistently across browsers. role=button
                // + keyboard handler preserves the a11y behaviour.
                const item = element(documentRef, 'div', 'code-analyzer-file-item');
                item.setAttribute('role', 'button');
                item.tabIndex = 0;
                item.classList.toggle('active', file === activeFile);
                item.title = file.path;
                setTone(item, file.tone);

                const railMark = element(documentRef, 'span', 'code-analyzer-severity-rail');
                const fileCopy = element(documentRef, 'span', 'code-analyzer-file-main');
                fileCopy.append(element(documentRef, 'strong', '', file.name));
                fileCopy.title = file.path;
                const issues = element(documentRef, 'span', 'code-analyzer-file-issues');
                const critical = file.metrics.filter(metric => concernSeverity(metric.score) === 'critical').length;
                const warnings = file.metrics.filter(metric => concernSeverity(metric.score) === 'warning').length;
                issues.textContent = `${critical} critical · ${warnings} warning${warnings === 1 ? '' : 's'}`;
                fileCopy.append(issues);
                const score = element(documentRef, 'span', 'code-analyzer-file-score');
                score.append(element(documentRef, 'strong', '', formatScore(file.health)), element(documentRef, 'small', '', 'quality'));
                item.append(railMark, fileCopy, score);
                if (typeof onIgnoreFile === 'function') {
                    item.append(buildKebab(`Actions for ${file.path}`, [{
                        icon: 'fa-solid fa-eye-slash',
                        label: 'Ignore file',
                        onPick: () => onIgnoreFile(file)
                    }]));
                }

                const openFile = () => onSelect(file);
                item.addEventListener?.('click', event => {
                    // Kebab clicks stop their own propagation; the guard covers hosts
                    // where synthetic events still bubble.
                    if (event.target?.closest?.('.code-analyzer-kebab')) return;
                    openFile();
                });
                item.addEventListener?.('keydown', event => {
                    if (event.target !== event.currentTarget) return;
                    if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault?.();
                        openFile();
                    }
                });
                list.append(item);
            }
        }
    };
    // Debounced: paintList rebuilds every row in the rail, and doing that on each
    // keystroke makes typing in the filter feel sticky on large scans / slow machines.
    let filterPaintTimer = null;
    input.addEventListener?.('input', () => {
        if (filterPaintTimer) clearTimeout(filterPaintTimer);
        filterPaintTimer = setTimeout(() => {
            filterPaintTimer = null;
            paintList();
        }, 120);
    });
    for (const button of filterButtons) {
        button.addEventListener?.('click', () => {
            activeFilter = button.dataset.filter;
            for (const candidate of filterButtons) {
                const active = candidate === button;
                candidate.classList.toggle('active', active);
                candidate.setAttribute('aria-pressed', String(active));
            }
            paintList();
        });
    }
    paintList();

    // Files the user removed from results — always discoverable and restorable.
    const ignoredBox = renderIgnoredFilesBox(documentRef, ignoredFiles, onRestoreFile);
    if (ignoredBox) rail.append(ignoredBox);
    rail.append(contextMenu);

    rail.refresh = paintList;
    rail.setSelectedFile = (file) => {
        activeFile = file;
        paintList();
    };
    rail.destroy = () => {
        closeContextMenu();
        documentRef.removeEventListener?.('click', onDocumentClick);
    };
    return rail;
}

/**
 * Renders the Code Quality workspace (changed files | metrics | source) and returns
 * the number of files shown. The compact scan summary lives on the Validation screen
 * (renderCodeAnalyzerBrief) — this surface is all detail.
 * options.fetchSource(path) → Promise of /api/v1/code-analyzer/source's response; when
 * provided, the source pane shows the WHOLE file and metric clicks jump to their line.
 * Without it the pane falls back to the report's snippet.
 */
export function renderCodeAnalyzerDashboard(container, response, documentRef = globalThis.document, options = {}) {
    if (!container || !documentRef?.createElement) return 0;
    disposeCodeAnalyzerDashboard(container);
    const model = buildCodeAnalyzerDashboardModel(response);
    container.replaceChildren?.();
    if (!model.files.length) {
        // Every changed file is ignored → the report is empty, but the user still needs a way to
        // restore. Render the standalone "Ignored files" box so the only restore UI stays reachable
        // (the controller keeps the container visible whenever ignores exist).
        const ignoredOnly = Array.isArray(options.ignoredFiles) ? options.ignoredFiles : [];
        const onRestoreEarly = typeof options.onRestoreFile === 'function' ? options.onRestoreFile : null;
        const box = renderIgnoredFilesBox(documentRef, ignoredOnly, onRestoreEarly);
        if (box) container.append(box);
        return 0;
    }

    const fetchSource = typeof options.fetchSource === 'function' ? options.fetchSource : null;
    const ignoredFiles = Array.isArray(options.ignoredFiles) ? options.ignoredFiles : [];
    const onIgnoreFile = typeof options.onIgnoreFile === 'function' ? options.onIgnoreFile : null;
    const onIgnoreDirectory = typeof options.onIgnoreDirectory === 'function' ? options.onIgnoreDirectory : null;
    const onRestoreFile = typeof options.onRestoreFile === 'function' ? options.onRestoreFile : null;
    // preserveState lets the controller keep the user's place across re-renders
    // (e.g. after an ignore + rescan). Without it the dashboard always boots with
    // the first file selected, which the user reads as being "kicked out" after
    // every ignore action.
    const preserveState = options.preserveState && typeof options.preserveState === 'object'
        ? options.preserveState
        : null;
    // onStateChange fires whenever the user moves to a different file, metric, or
    // tab, so the controller can capture the latest state for the next re-render.
    const onStateChange = typeof options.onStateChange === 'function' ? options.onStateChange : null;
    const session = {
        container,
        monaco: null,
        editor: null,
        model: null,
        decorations: null,
        editorFilePath: null,
        editorHasFullSource: false,
        // Unhooks the rail's document-level click-away listener on dispose.
        disposeRail: null,
        generation: 0,
        disposed: false
    };
    CODE_ANALYZER_SESSIONS.set(container, session);
    const canMountMonaco = documentRef === globalThis.document && typeof globalThis.window !== 'undefined';

    const workspace = element(documentRef, 'div', 'code-analyzer-workspace');
    // The rail is absolutely positioned inside this cell so its (potentially very
    // long) file list can never drive the workspace row height — the detail columns
    // set the height and the rail scrolls inside it.
    const railCell = element(documentRef, 'div', 'code-analyzer-rail-cell');
    const review = element(documentRef, 'section', 'code-analyzer-review-column');
    const sourceColumn = element(documentRef, 'aside', 'code-analyzer-source-column');

    // Resolve the initial selection from preserveState if possible. If the
    // previously-selected file is gone (ignored), pick the next file in the
    // model's order at the same index — so ignoring one file in a long list
    // drops the user on the next one instead of bouncing back to file #1.
    const filesByPath = new Map(model.files.map(file => [file.path, file]));
    let initialFile = model.files[0];
    let initialMetric = initialFile?.priorityMetric || initialFile?.metrics[0] || null;
    if (preserveState?.selectedFilePath) {
        const preserved = filesByPath.get(preserveState.selectedFilePath);
        if (preserved) {
            initialFile = preserved;
            if (preserveState.selectedMetricName) {
                const metricMatch = preserved.metrics.find(metric => metric.name === preserveState.selectedMetricName);
                if (metricMatch) initialMetric = metricMatch;
            } else {
                initialMetric = preserved.priorityMetric || preserved.metrics[0] || null;
            }
        } else if (model.files.length) {
            // The selected file is gone — fall back to the next file at the same
            // index (clamped), which is usually the next thing the user wanted.
            const previousIndex = Number.isFinite(preserveState.selectedIndex) ? preserveState.selectedIndex : 0;
            const fallbackIndex = Math.min(previousIndex, model.files.length - 1);
            initialFile = model.files[fallbackIndex] || model.files[0];
            initialMetric = initialFile.priorityMetric || initialFile.metrics[0] || null;
        }
    }
    let selectedFile = initialFile;
    let selectedMetric = initialMetric;
    let rail;
    let evidenceView = null;
    // Full file contents by path; null marks "asked, not available" so a metric click
    // never refetches a file that already failed.
    const sourceCache = new Map();

    const emitState = () => {
        if (typeof onStateChange !== 'function') return;
        onStateChange({
            selectedFilePath: selectedFile?.path || null,
            selectedMetricName: selectedMetric?.name || null,
            selectedIndex: selectedFile ? model.files.indexOf(selectedFile) : -1
        });
    };

    async function loadFullSource(path) {
        if (sourceCache.has(path)) return sourceCache.get(path);
        let content = null;
        if (fetchSource) {
            try {
                const sourceResponse = await fetchSource(path);
                content = typeof sourceResponse?.content === 'string' ? sourceResponse.content : null;
            } catch {
                content = null;
            }
        }
        sourceCache.set(path, content);
        return content;
    }

    function mountEvidence() {
        if (!evidenceView) return;
        if (!canMountMonaco) {
            showEvidenceFallback(evidenceView);
            return;
        }
        if (session.editor) {
            globalThis.requestAnimationFrame?.(() => session.editor?.layout?.());
            return;
        }
        void mountSourceEditor();
    }

    async function mountSourceEditor() {
        const view = evidenceView;
        const file = selectedFile;
        const metric = selectedMetric;
        const ticket = session.generation;
        const fullSource = await loadFullSource(file.path);
        if (session.disposed || ticket !== session.generation || evidenceView !== view) return;
        await mountCodeEvidenceEditor(view, file, metric, session, fullSource);
    }

    const paintEvidence = () => {
        disposeEvidenceEditor(session);
        sourceColumn.replaceChildren();
        evidenceView = renderSourcePane(documentRef, selectedFile, selectedMetric);
        sourceColumn.append(evidenceView.panel);
        mountEvidence();
    };

    // A metric click inside the already-loaded file just moves the highlight and scrolls;
    // the editor is not rebuilt. Anything else falls back to a full repaint.
    const retargetEvidence = () => {
        const canRetarget = session.editor
            && session.monaco
            && session.editorHasFullSource
            && session.editorFilePath === selectedFile.path
            && evidenceView;
        if (!canRetarget) {
            paintEvidence();
            return;
        }

        const freshHeader = renderSourceHeader(documentRef, selectedFile, selectedMetric);
        evidenceView.header.replaceWith?.(freshHeader);
        evidenceView.header = freshHeader;
        if (evidenceView.position) {
            evidenceView.position.textContent = Number.isFinite(selectedMetric?.line) && selectedMetric.line > 0
                ? `Line ${selectedMetric.line}`
                : selectedFile.path;
        }

        const line = Number.isFinite(selectedMetric?.line) && selectedMetric.line > 0 ? selectedMetric.line : null;
        if (line) {
            const range = new session.monaco.Range(line, 1, line, 1);
            const decorationOptions = evidenceDecorationOptions(session.monaco, mintLintConcernTone(selectedMetric.score));
            if (session.decorations?.set) {
                session.decorations.set([{ range, options: decorationOptions }]);
            } else {
                session.decorations = session.editor.createDecorationsCollection?.([
                    { range, options: decorationOptions }
                ]) || null;
            }
            session.editor.setSelection?.(range);
            session.editor.revealLineNearTop?.(line);
        } else {
            // No line (file-level / NA metric): clear the marker and go to the top.
            try { session.decorations?.clear?.(); } catch (_) { /* no-op */ }
            session.editor.revealLineNearTop?.(1);
        }
    };

    const paintReview = () => {
        review.replaceChildren();
        review.append(renderFileOverview(documentRef, selectedFile, onIgnoreFile, onIgnoreDirectory));
        if (!selectedMetric) {
            review.append(element(documentRef, 'div', 'code-analyzer-panel-surface code-analyzer-no-metrics', 'No metric detail was returned for this file.'));
            return;
        }
        const selectMetric = metric => {
            selectedMetric = metric;
            paintReview();
            retargetEvidence();
            emitState();
        };
        review.append(renderMetricsPanel(documentRef, selectedFile, selectedMetric, selectMetric));
    };

    const selectFile = file => {
        selectedFile = file;
        selectedMetric = file.priorityMetric || file.metrics[0] || null;
        rail?.setSelectedFile?.(file);
        paintReview();
        paintEvidence();
        emitState();
    };
    rail = renderFileRail(documentRef, model, selectedFile, selectFile, ignoredFiles, {
        onRestoreFile,
        onIgnoreFile,
        onIgnoreDirectories: onIgnoreDirectory ? (paths => onIgnoreDirectory({ directoryPaths: paths })) : null
    });
    session.disposeRail = typeof rail.destroy === 'function' ? rail.destroy : null;
    railCell.append(rail);
    workspace.append(railCell, review, sourceColumn);
    container.append(workspace);
    paintReview();
    paintEvidence();
    return model.files.length;
}
