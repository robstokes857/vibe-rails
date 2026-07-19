import {
    buildMintLintReportViewModel,
    mintLintConcernTone
} from './git-guard-preflight.js';
import { ensureMonaco } from './monaco-loader.js';

const CODE_ANALYZER_SESSIONS = new WeakMap();
let codeAnalyzerDashboardInstance = 0;

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

function clampScore(value) {
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
    if (lastSeparator < 0) return { folder: 'repository root', name: value };
    return {
        folder: value.slice(0, lastSeparator) || 'repository root',
        name: value.slice(lastSeparator + 1) || value
    };
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
            priorityMetric: worstMetric(metrics)
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

function renderScanBanner(documentRef, model) {
    const banner = element(documentRef, 'section', 'code-analyzer-scan-banner');

    const scoreCard = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-score-card');
    setTone(scoreCard, model.tone);
    const ring = element(documentRef, 'div', 'code-analyzer-score-ring');
    setTone(ring, model.tone);
    setStyleProperty(ring, '--score', String(model.health || 0));
    const center = element(documentRef, 'div', 'code-analyzer-score-center');
    center.append(
        element(documentRef, 'strong', 'code-analyzer-score-value', formatScore(model.health)),
        element(documentRef, 'span', 'code-analyzer-score-label', 'quality score'));
    ring.append(center);

    const copy = element(documentRef, 'div', 'code-analyzer-score-copy');
    copy.append(
        element(documentRef, 'span', 'code-analyzer-eyebrow', 'Change quality'),
        element(documentRef, 'h3', '', model.healthLabel),
        element(documentRef, 'p', '', `${model.analyzedFileCount} changed source file${model.analyzedFileCount === 1 ? '' : 's'} analyzed. Focus the review on the highest-risk hotspots.`));
    const gradeLine = element(documentRef, 'div', 'code-analyzer-grade-line');
    const grade = element(documentRef, 'span', 'code-analyzer-rating-badge', model.qualityGrade);
    setTone(grade, model.tone);
    const gradeCopy = element(documentRef, 'span', 'code-analyzer-grade-copy');
    gradeCopy.append(
        element(documentRef, 'strong', '', model.rating),
        element(documentRef, 'small', '', model.criticalCount
            ? `${model.criticalCount} critical metric${model.criticalCount === 1 ? '' : 's'} need attention`
            : 'No critical metrics in this scan'));
    gradeLine.append(grade, gradeCopy);
    copy.append(gradeLine);
    scoreCard.append(ring, copy);

    // Under the grade: a one-row-per-category digest of where the changeset's risk sits.
    // (The fixed metric roster lives in the main Scan overview box to the right.)
    if (model.categories.length) {
        const digest = element(documentRef, 'div', 'code-analyzer-scorecard is-categories');
        const head = element(documentRef, 'div', 'code-analyzer-scorecard-row code-analyzer-scorecard-head');
        head.append(
            element(documentRef, 'i'),
            element(documentRef, 'span', '', 'Category'),
            element(documentRef, 'span', '', 'Risk'));
        digest.append(head);

        for (const category of model.categories) {
            const row = element(documentRef, 'div', 'code-analyzer-scorecard-row');
            setTone(row, mintLintConcernTone(category.concern));

            const dot = element(documentRef, 'i', 'code-analyzer-scorecard-dot');
            const label = element(documentRef, 'span', 'code-analyzer-scorecard-label');
            label.append(element(documentRef, 'strong', '', category.name));
            if (category.worstLabel) {
                label.title = `Worst signal: ${category.worstLabel}${category.worstFile ? ` in ${category.worstFile}` : ''}`;
            }

            const risk = element(documentRef, 'span', 'code-analyzer-scorecard-concern', formatScore(category.concern));
            row.append(dot, label, risk);
            digest.append(row);
        }
        scoreCard.append(digest);
    }

    const overview = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-overview-card');
    const overviewHead = element(documentRef, 'div', 'code-analyzer-overview-head');
    const overviewCopy = element(documentRef, 'div');
    overviewCopy.append(
        element(documentRef, 'span', 'code-analyzer-eyebrow', 'Scan overview'),
        element(documentRef, 'h3', '', 'What changed, and where risk is concentrated'));
    const scanTime = element(documentRef, 'span', 'code-analyzer-scan-time');
    scanTime.textContent = [model.scannedAt ? `Scanned ${model.scannedAt}` : '', model.duration ? `Completed in ${model.duration}` : '']
        .filter(Boolean).join(' · ');
    overviewHead.append(overviewCopy, scanTime);
    overview.append(overviewHead);

    const stats = [
        [model.analyzedFileCount, 'Analyzed', 'neutral'],
        [model.skippedFileCount, 'Skipped', 'neutral'],
        [model.criticalCount, 'Critical', 'danger'],
        [model.warningCount, 'Warnings', 'warning'],
        [model.healthyFileCount, 'Healthy files', 'success']
    ];
    if (model.ignoredFileCount > 0) {
        stats.splice(2, 0, [model.ignoredFileCount, 'Ignored', 'neutral']);
    }
    const statStrip = element(documentRef, 'div', 'code-analyzer-stat-strip');
    for (const [value, label, tone] of stats) {
        const card = element(documentRef, 'div', 'code-analyzer-stat-card');
        setTone(card, tone);
        card.append(element(documentRef, 'strong', '', value), element(documentRef, 'span', '', label));
        statStrip.append(card);
    }
    overview.append(statStrip);

    // The main grid: the fixed metric roster, always all eight in this order, showing
    // changeset AVERAGES. Cards lead with a severity WORD so "Testability · 100" can
    // never read like a compliment; the direction tag explains the raw measurement.
    const scorecardGrid = element(documentRef, 'div', 'code-analyzer-category-grid');
    for (const entry of model.scorecard) {
        const tone = entry.measured ? mintLintConcernTone(entry.averageConcern) : 'neutral';
        const card = element(documentRef, 'div', 'code-analyzer-category-card');
        setTone(card, tone);
        if (!entry.measured) card.classList.add('is-unmeasured');
        if (entry.measured && entry.file) {
            card.title = `Worst in ${entry.file}${entry.line ? ` · line ${entry.line}` : ''}`;
        }

        const head = element(documentRef, 'div', 'code-analyzer-category-head');
        head.append(element(documentRef, 'span', 'code-analyzer-category-name', entry.label));
        const badge = element(documentRef, 'span', 'code-analyzer-category-severity',
            entry.measured ? concernSeverity(entry.averageConcern) : 'not measured');
        setTone(badge, tone);
        head.append(badge);
        card.append(head);

        const valueLine = element(documentRef, 'div', 'code-analyzer-category-worst');
        if (entry.measured) {
            // A combined card (Coupling, Testability) names the member metric that won.
            valueLine.append(element(documentRef, 'strong', '',
                entry.metricLabel && entry.metricLabel !== entry.label ? `via ${entry.metricLabel}` : 'Average'));
            valueLine.append(element(documentRef, 'span', '',
                formatMetricValue({ name: entry.metricName, value: entry.averageValue })));
            const hint = directionHint(entry.direction);
            if (hint) {
                const tag = element(documentRef, 'span', 'code-analyzer-direction-tag', `${hint.arrow} ${hint.text}`);
                tag.title = `The raw value reads "${hint.text}"; the risk score is always higher-is-worse.`;
                valueLine.append(tag);
            }
        } else {
            valueLine.append(element(documentRef, 'span', '', 'Not measured in this changeset'));
        }
        card.append(valueLine);

        const meter = element(documentRef, 'span', 'code-analyzer-mini-track');
        const fill = element(documentRef, 'span');
        setStyleProperty(fill, '--fill', `${entry.measured ? (clampScore(entry.averageConcern) || 0) : 0}%`);
        meter.append(fill);
        card.append(meter);

        const scoreRow = element(documentRef, 'div', 'code-analyzer-category-score-row');
        scoreRow.append(element(documentRef, 'small', '', 'avg risk'));
        const numbers = element(documentRef, 'span', 'code-analyzer-category-numbers');
        if (entry.measured) {
            numbers.append(element(documentRef, 'strong', '', formatScore(entry.averageConcern)));
            if (entry.worstConcern !== null && entry.worstConcern > entry.averageConcern + 0.05) {
                numbers.append(element(documentRef, 'small', '', `worst ${formatScore(entry.worstConcern)}`));
            }
        } else {
            numbers.append(element(documentRef, 'strong', '', '—'));
        }
        scoreRow.append(numbers);
        card.append(scoreRow);
        scorecardGrid.append(card);
    }
    overview.append(scorecardGrid);
    banner.append(scoreCard, overview);
    return banner;
}

/** Human label for an ignore reason ({reasonKind, reasonText}). */
function ignoreReasonLabel(entry) {
    if (entry.reasonKind === 'test') return 'Test files';
    if (entry.reasonKind === 'config') return 'Config file';
    if (entry.reasonKind === 'other') return entry.reasonText || 'Other';
    return entry.reasonText || '';
}

function renderFileOverview(documentRef, file, onIgnoreFile) {
    const overview = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-file-overview');
    const titleRow = element(documentRef, 'div', 'code-analyzer-file-title-row');
    const titleWrap = element(documentRef, 'div', 'code-analyzer-file-title-wrap');
    titleWrap.append(
        element(documentRef, 'span', 'code-analyzer-file-kicker', file.folder),
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

function renderFileRail(documentRef, model, selectedFile, onSelect, ignoredFiles = [], onRestoreFile = null) {
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
    const paintList = () => {
        const query = String(input.value || '').trim().toLowerCase();
        const visible = model.files.filter(file =>
            (activeFilter === 'all' || file.severity === activeFilter) &&
            (!query || file.path.toLowerCase().includes(query)));
        list.replaceChildren();
        if (!visible.length) {
            list.append(element(documentRef, 'p', 'code-analyzer-file-list-empty', 'No files match this filter.'));
            return;
        }
        for (const file of visible) {
            const button = element(documentRef, 'button', 'code-analyzer-file-item');
            button.type = 'button';
            button.classList.toggle('active', file === selectedFile);
            setTone(button, file.tone);
            const railMark = element(documentRef, 'span', 'code-analyzer-severity-rail');
            const fileCopy = element(documentRef, 'span', 'code-analyzer-file-main');
            fileCopy.append(
                element(documentRef, 'strong', '', file.name),
                element(documentRef, 'small', '', file.folder));
            const issues = element(documentRef, 'span', 'code-analyzer-file-issues');
            const critical = file.metrics.filter(metric => concernSeverity(metric.score) === 'critical').length;
            const warnings = file.metrics.filter(metric => concernSeverity(metric.score) === 'warning').length;
            issues.textContent = `${critical} critical · ${warnings} warning${warnings === 1 ? '' : 's'}`;
            fileCopy.append(issues);
            const score = element(documentRef, 'span', 'code-analyzer-file-score');
            score.append(element(documentRef, 'strong', '', formatScore(file.health)), element(documentRef, 'small', '', 'quality'));
            button.append(railMark, fileCopy, score);
            button.addEventListener?.('click', () => onSelect(file));
            list.append(button);
        }
    };
    input.addEventListener?.('input', paintList);
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
    if (ignoredFiles.length) {
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
        rail.append(ignoredBox);
    }

    rail.refresh = paintList;
    return rail;
}

/**
 * Renders the tabbed Code Quality dashboard and returns the number of files shown.
 * options.fetchSource(path) → Promise of /api/v1/code-analyzer/source's response; when
 * provided, the source pane shows the WHOLE file and metric clicks jump to their line.
 * Without it the pane falls back to the report's snippet.
 */
export function renderCodeAnalyzerDashboard(container, response, documentRef = globalThis.document, options = {}) {
    if (!container || !documentRef?.createElement) return 0;
    disposeCodeAnalyzerDashboard(container);
    const model = buildCodeAnalyzerDashboardModel(response);
    container.replaceChildren?.();
    if (!model.files.length) return 0;

    const fetchSource = typeof options.fetchSource === 'function' ? options.fetchSource : null;
    const ignoredFiles = Array.isArray(options.ignoredFiles) ? options.ignoredFiles : [];
    const onIgnoreFile = typeof options.onIgnoreFile === 'function' ? options.onIgnoreFile : null;
    const onRestoreFile = typeof options.onRestoreFile === 'function' ? options.onRestoreFile : null;
    const session = {
        container,
        monaco: null,
        editor: null,
        model: null,
        decorations: null,
        editorFilePath: null,
        editorHasFullSource: false,
        generation: 0,
        disposed: false
    };
    CODE_ANALYZER_SESSIONS.set(container, session);
    const canMountMonaco = documentRef === globalThis.document && typeof globalThis.window !== 'undefined';

    const instanceId = ++codeAnalyzerDashboardInstance;
    const tabDefinitions = [
        { id: 'overview', label: 'Overview', icon: 'fa-solid fa-chart-pie' },
        { id: 'findings', label: 'Files & metrics', icon: 'fa-solid fa-list-check', count: model.files.length }
    ];
    const tabList = element(documentRef, 'div', 'code-analyzer-tabs');
    tabList.setAttribute('role', 'tablist');
    tabList.setAttribute('aria-label', 'Code quality report sections');
    const tabButtons = [];
    const tabPanels = new Map();
    const panelStack = element(documentRef, 'div', 'code-analyzer-tab-panels');

    for (const definition of tabDefinitions) {
        const tabId = `code-quality-tab-${instanceId}-${definition.id}`;
        const panelId = `code-quality-panel-${instanceId}-${definition.id}`;
        const button = element(documentRef, 'button', 'code-analyzer-tab');
        button.type = 'button';
        button.id = tabId;
        button.dataset.codeAnalyzerTab = definition.id;
        button.setAttribute('role', 'tab');
        button.setAttribute('aria-controls', panelId);
        button.setAttribute('aria-selected', 'false');
        button.tabIndex = -1;
        button.append(icon(documentRef, definition.icon), element(documentRef, 'span', '', definition.label));
        if (Number.isFinite(definition.count)) {
            button.append(element(documentRef, 'span', 'code-analyzer-tab-count', definition.count));
        }
        tabList.append(button);
        tabButtons.push(button);

        const panel = element(documentRef, 'section', `code-analyzer-tab-panel code-analyzer-${definition.id}-panel`);
        panel.id = panelId;
        panel.dataset.codeAnalyzerTabPanel = definition.id;
        panel.setAttribute('role', 'tabpanel');
        panel.setAttribute('aria-labelledby', tabId);
        panel.tabIndex = 0;
        panel.hidden = true;
        panelStack.append(panel);
        tabPanels.set(definition.id, panel);
    }

    container.append(tabList, panelStack);
    const overviewPanel = tabPanels.get('overview');
    const findingsPanel = tabPanels.get('findings');
    overviewPanel.append(renderScanBanner(documentRef, model));

    const workspace = element(documentRef, 'div', 'code-analyzer-workspace');
    const review = element(documentRef, 'section', 'code-analyzer-review-column');
    const sourceColumn = element(documentRef, 'aside', 'code-analyzer-source-column');
    let selectedFile = model.files[0];
    let selectedMetric = selectedFile.priorityMetric || selectedFile.metrics[0] || null;
    let rail;
    let activeTab = 'overview';
    let evidenceView = null;
    // Full file contents by path; null marks "asked, not available" so a metric click
    // never refetches a file that already failed.
    const sourceCache = new Map();

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

    function mountEvidenceWhenVisible() {
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

    function activateTab(tabId, { focus = false } = {}) {
        if (!tabPanels.has(tabId)) return;
        activeTab = tabId;
        for (const button of tabButtons) {
            const isActive = button.dataset.codeAnalyzerTab === tabId;
            button.classList.toggle('active', isActive);
            button.setAttribute('aria-selected', String(isActive));
            button.tabIndex = isActive ? 0 : -1;
            if (isActive && focus) button.focus?.();
        }
        for (const [id, panel] of tabPanels) panel.hidden = id !== tabId;
        if (tabId === 'findings') mountEvidenceWhenVisible();
    }

    for (const button of tabButtons) {
        button.addEventListener?.('click', () => activateTab(button.dataset.codeAnalyzerTab));
        button.addEventListener?.('keydown', event => {
            const currentIndex = tabButtons.indexOf(button);
            let nextIndex = null;
            if (event.key === 'ArrowRight') nextIndex = (currentIndex + 1) % tabButtons.length;
            if (event.key === 'ArrowLeft') nextIndex = (currentIndex - 1 + tabButtons.length) % tabButtons.length;
            if (event.key === 'Home') nextIndex = 0;
            if (event.key === 'End') nextIndex = tabButtons.length - 1;
            if (nextIndex === null) return;
            event.preventDefault?.();
            activateTab(tabButtons[nextIndex].dataset.codeAnalyzerTab, { focus: true });
        });
    }

    const paintEvidence = () => {
        disposeEvidenceEditor(session);
        sourceColumn.replaceChildren();
        evidenceView = renderSourcePane(documentRef, selectedFile, selectedMetric);
        sourceColumn.append(evidenceView.panel);
        if (activeTab === 'findings') mountEvidenceWhenVisible();
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
        review.append(renderFileOverview(documentRef, selectedFile, onIgnoreFile));
        if (!selectedMetric) {
            review.append(element(documentRef, 'div', 'code-analyzer-panel-surface code-analyzer-no-metrics', 'No metric detail was returned for this file.'));
            return;
        }
        const selectMetric = metric => {
            selectedMetric = metric;
            paintReview();
            retargetEvidence();
        };
        review.append(renderMetricsPanel(documentRef, selectedFile, selectedMetric, selectMetric));
    };

    const selectFile = file => {
        selectedFile = file;
        selectedMetric = file.priorityMetric || file.metrics[0] || null;
        rail?.refresh?.();
        paintReview();
        paintEvidence();
    };
    rail = renderFileRail(documentRef, model, selectedFile, selectFile, ignoredFiles, onRestoreFile);
    workspace.append(rail, review, sourceColumn);
    findingsPanel.append(workspace);
    paintReview();
    paintEvidence();
    activateTab('overview');
    return model.files.length;
}
