import {
    buildMintLintReportViewModel,
    mintLintConcernTone
} from './git-guard-preflight.js';

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

/** Builds the scan-oriented view model used by the Rules-page MintLint dashboard. */
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
                    snippet: worst?.snippet || ''
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
    const categoryMap = new Map();
    for (const file of files) {
        for (const category of file.categories) {
            const existing = categoryMap.get(category.name);
            if (!existing || Number(category.score) > Number(existing.concern)) {
                const metric = worstMetric(category.metrics);
                categoryMap.set(category.name, {
                    name: category.name,
                    concern: clampScore(category.score),
                    worst: metric ? `${metric.label} · ${formatMetricValue(metric)}` : 'No notable concerns'
                });
            }
        }
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
        healthLabel: health === null ? 'No staged code' : health >= 85 ? 'Healthy change' : health >= 70 ? 'Review ready' : health >= 45 ? 'Needs attention' : 'Change required',
        rating,
        qualityGrade: qualityGrade(health),
        tone: qualityTone(health),
        analyzedFileCount,
        skippedFileCount,
        criticalCount,
        warningCount,
        healthyFileCount,
        categories: [...categoryMap.values()],
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
        element(documentRef, 'p', '', `${model.analyzedFileCount} staged source file${model.analyzedFileCount === 1 ? '' : 's'} analyzed. Focus the review on the highest-concern hotspots.`));
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
    const statStrip = element(documentRef, 'div', 'code-analyzer-stat-strip');
    for (const [value, label, tone] of stats) {
        const card = element(documentRef, 'div', 'code-analyzer-stat-card');
        setTone(card, tone);
        card.append(element(documentRef, 'strong', '', value), element(documentRef, 'span', '', label));
        statStrip.append(card);
    }
    overview.append(statStrip);

    const categoryGrid = element(documentRef, 'div', 'code-analyzer-category-grid');
    for (const category of model.categories) {
        const tone = mintLintConcernTone(category.concern);
        const card = element(documentRef, 'div', 'code-analyzer-category-card');
        setTone(card, tone);
        card.title = category.worst;
        card.append(element(documentRef, 'span', 'code-analyzer-category-name', category.name));
        const scoreRow = element(documentRef, 'div', 'code-analyzer-category-score-row');
        scoreRow.append(
            element(documentRef, 'strong', '', formatScore(category.concern)),
            element(documentRef, 'span', '', 'concern'));
        card.append(scoreRow);
        const meter = element(documentRef, 'span', 'code-analyzer-mini-track');
        const fill = element(documentRef, 'span');
        setStyleProperty(fill, '--fill', `${clampScore(category.concern) || 0}%`);
        meter.append(fill);
        card.append(meter, element(documentRef, 'small', '', category.worst));
        categoryGrid.append(card);
    }
    overview.append(categoryGrid);
    banner.append(scoreCard, overview);
    return banner;
}

function renderFileOverview(documentRef, file) {
    const overview = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-file-overview');
    const titleRow = element(documentRef, 'div', 'code-analyzer-file-title-row');
    const titleWrap = element(documentRef, 'div', 'code-analyzer-file-title-wrap');
    titleWrap.append(
        element(documentRef, 'span', 'code-analyzer-file-kicker', file.folder),
        element(documentRef, 'h3', 'code-analyzer-file-title', file.name));
    const meta = element(documentRef, 'div', 'code-analyzer-inline-meta');
    appendChip(documentRef, meta, 'Used by', `${file.referencedBy} file${file.referencedBy === 1 ? '' : 's'}`);
    if (Number.isFinite(file.priority)) appendChip(documentRef, meta, 'Priority', file.priority.toFixed(1));
    if (Number.isFinite(file.baseline)) appendChip(documentRef, meta, 'Baseline concern', file.baseline.toFixed(1));
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
            element(documentRef, 'small', '', `${formatMetricValue(file.priorityMetric)} measured · ${formatScore(file.priorityMetric.score)} normalized concern`));
        const location = element(documentRef, 'span', 'code-analyzer-priority-location');
        location.append(
            element(documentRef, 'strong', '', file.priorityMetric.source || file.name),
            element(documentRef, 'small', '', file.priorityMetric.line ? `Line ${file.priorityMetric.line}` : file.ratingLabel));
        finding.append(mark, findingCopy, location);
        overview.append(finding);
    }
    return overview;
}

function renderInspector(documentRef, file, metric) {
    const inspector = element(documentRef, 'aside', 'code-analyzer-panel-surface code-analyzer-inspector-panel');
    const header = element(documentRef, 'header', 'code-analyzer-inspector-head');
    header.append(
        element(documentRef, 'span', 'code-analyzer-eyebrow', metric.category),
        element(documentRef, 'h3', '', metric.label),
        element(documentRef, 'p', '', METRIC_GUIDANCE[metric.name]?.description || 'This metric is normalized so a higher concern score always indicates more review risk.'));
    const severity = element(documentRef, 'span', 'code-analyzer-severity-badge', concernSeverity(metric.score));
    setTone(severity, mintLintConcernTone(metric.score));
    header.append(severity);
    inspector.append(header);

    const body = element(documentRef, 'div', 'code-analyzer-inspector-body');
    const scoreCard = element(documentRef, 'div', 'code-analyzer-metric-score-card');
    const raw = element(documentRef, 'span');
    raw.append(element(documentRef, 'small', '', 'Measured value'), element(documentRef, 'strong', '', formatMetricValue(metric)));
    const concern = element(documentRef, 'span', 'code-analyzer-concern-number');
    setTone(concern, mintLintConcernTone(metric.score));
    concern.append(element(documentRef, 'strong', '', formatScore(metric.score)), element(documentRef, 'small', '', 'normalized concern'));
    scoreCard.append(raw, concern);
    body.append(scoreCard);

    const thresholdSection = element(documentRef, 'section', 'code-analyzer-inspector-section');
    thresholdSection.append(element(documentRef, 'span', 'code-analyzer-section-label', 'Thresholds'));
    const thresholdStack = element(documentRef, 'div', 'code-analyzer-threshold-stack');
    const comparator = metric.higherIsBetter ? '≤' : '≥';
    for (const [tone, label, value] of [
        ['success', 'Healthy', metric.warn],
        ['warning', 'Warning', metric.warn],
        ['danger', 'Critical', metric.critical]
    ]) {
        const line = element(documentRef, 'div', 'code-analyzer-threshold-line');
        const dot = element(documentRef, 'i');
        setTone(dot, tone);
        const display = tone === 'success'
            ? `${metric.higherIsBetter ? '>' : '<'} ${formatThreshold(value, metric)}`
            : `${comparator} ${formatThreshold(value, metric)}`;
        line.append(dot, element(documentRef, 'span', '', label), element(documentRef, 'strong', '', display));
        thresholdStack.append(line);
    }
    thresholdSection.append(thresholdStack);
    body.append(thresholdSection);

    const where = element(documentRef, 'section', 'code-analyzer-inspector-section');
    where.append(element(documentRef, 'span', 'code-analyzer-section-label', 'Where'));
    const whereBox = element(documentRef, 'div', 'code-analyzer-where-box');
    whereBox.append(
        element(documentRef, 'strong', '', metric.source || file.path),
        element(documentRef, 'span', '', metric.line ? `${file.path} · line ${metric.line}` : file.path));
    where.append(whereBox);
    body.append(where);

    const action = element(documentRef, 'section', 'code-analyzer-inspector-section');
    action.append(
        element(documentRef, 'span', 'code-analyzer-section-label', 'Recommended direction'),
        element(documentRef, 'p', '', METRIC_GUIDANCE[metric.name]?.action || 'Review the measured location and reduce the strongest contributor before broad refactoring.'));
    body.append(action);
    inspector.append(body);
    return inspector;
}

function renderCodeEvidence(documentRef, file, metric) {
    const panel = element(documentRef, 'article', 'code-analyzer-panel-surface code-analyzer-code-panel');
    const header = element(documentRef, 'header', 'code-analyzer-code-head');
    const copy = element(documentRef, 'div');
    copy.append(
        element(documentRef, 'h3', '', 'Code evidence'),
        element(documentRef, 'p', '', metric.snippet
            ? 'The analyzer returned this source excerpt for the selected worst-offender metric.'
            : 'The selected metric includes a location, but this scan did not return a source excerpt for it.'));
    const location = element(documentRef, 'span', 'code-analyzer-evidence-location');
    location.textContent = [metric.source || file.name, metric.line ? `L${metric.line}` : ''].filter(Boolean).join(' · ');
    header.append(copy, location);
    panel.append(header);
    const code = element(documentRef, 'pre', 'code-analyzer-code-evidence');
    code.textContent = metric.snippet || `${file.path}\n\nNo code snippet was returned for ${metric.label}.`;
    panel.append(code);
    return panel;
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
            element(documentRef, 'span', '', `${formatScore(category.score)} category concern`));
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
            concern.append(element(documentRef, 'small', '', 'concern'), element(documentRef, 'strong', '', formatScore(metric.score)));
            const meter = element(documentRef, 'span', 'code-analyzer-risk-track');
            const fill = element(documentRef, 'span');
            setStyleProperty(fill, '--risk', `${clampScore(metric.score) || 0}%`);
            meter.append(fill);
            const threshold = element(documentRef, 'span', 'code-analyzer-metric-threshold');
            threshold.append(
                element(documentRef, 'strong', '', concernSeverity(metric.score)),
                element(documentRef, 'small', '', `W ${formatThreshold(metric.warn, metric)} · C ${formatThreshold(metric.critical, metric)}`));
            row.append(title, element(documentRef, 'span', 'code-analyzer-metric-raw', formatMetricValue(metric)), concern, meter, threshold);
            row.addEventListener?.('click', () => onSelect(metric));
            list.append(row);
        }
        group.append(list);
        groups.append(group);
    }
    panel.append(groups);
    return panel;
}

function renderFileRail(documentRef, model, selectedFile, onSelect) {
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
    rail.refresh = paintList;
    return rail;
}

/** Renders the interactive MintLint dashboard and returns the number of files shown. */
export function renderCodeAnalyzerDashboard(container, response, documentRef = globalThis.document) {
    if (!container || !documentRef?.createElement) return 0;
    const model = buildCodeAnalyzerDashboardModel(response);
    container.replaceChildren?.();
    if (!model.files.length) return 0;

    container.append(renderScanBanner(documentRef, model));
    const workspace = element(documentRef, 'div', 'code-analyzer-workspace');
    const review = element(documentRef, 'section', 'code-analyzer-review-column');
    let selectedFile = model.files[0];
    let selectedMetric = selectedFile.priorityMetric || selectedFile.metrics[0] || null;
    let rail;

    const paintReview = () => {
        review.replaceChildren();
        review.append(renderFileOverview(documentRef, selectedFile));
        if (!selectedMetric) {
            review.append(element(documentRef, 'div', 'code-analyzer-panel-surface code-analyzer-no-metrics', 'No metric detail was returned for this file.'));
            return;
        }
        const detail = element(documentRef, 'div', 'code-analyzer-detail-grid');
        const selectMetric = metric => {
            selectedMetric = metric;
            paintReview();
        };
        detail.append(
            renderMetricsPanel(documentRef, selectedFile, selectedMetric, selectMetric),
            renderInspector(documentRef, selectedFile, selectedMetric));
        review.append(detail, renderCodeEvidence(documentRef, selectedFile, selectedMetric));
    };

    const selectFile = file => {
        selectedFile = file;
        selectedMetric = file.priorityMetric || file.metrics[0] || null;
        rail?.refresh?.();
        paintReview();
    };
    rail = renderFileRail(documentRef, model, selectedFile, selectFile);
    workspace.append(rail, review);
    container.append(workspace);
    paintReview();
    return model.files.length;
}
