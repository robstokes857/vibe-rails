// The existing analyzer response is the only data source.
export const CATEGORY_NAMES = ['Complexity','Size','Cohesion','Coupling','Testability','Duplication','Maintainability'];
export const isNumber = value => typeof value === 'number' && Number.isFinite(value);
const numeric = value => isNumber(value) ? value : null;
const count = value => isNumber(value) && value >= 0 && Number.isInteger(value) ? value : null;
const text = value => typeof value === 'string' ? value : '';
export const clamp = value => Math.min(100, Math.max(0, value));
export const healthFromConcern = value => isNumber(value) ? 100 - clamp(value) : null;
export function gradeFromHealth(value) {
  if (!isNumber(value)) return '—';
  return value >= 90 ? 'A' : value >= 80 ? 'B' : value >= 70 ? 'C' : value >= 55 ? 'D' : 'F';
}
const formats = new Map();
export function formatNumber(value, digits = 3) {
  if (!isNumber(value)) return '—';
  if (!formats.has(digits)) formats.set(digits, new Intl.NumberFormat('en-US', {maximumFractionDigits:digits}));
  return formats.get(digits).format(value);
}
export const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
export function adaptApiResponse(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Invalid analyzer response.');
  if (input.success === false || (isNumber(input.exitCode) && input.exitCode !== 0) || ['failed','error'].includes(input.status)) throw new Error('Code analysis failed.');
  if (!input.report || !Array.isArray(input.report.files)) throw new Error('Analyzer response is missing report.files.');
  // Own a snapshot, so host mutations cannot change a rendered report.
  const response = structuredClone(input);
  const raw = response.report;
  const overview = Array.isArray(raw.overview) ? raw.overview : [];
  const categories = CATEGORY_NAMES.map(name => {
    const source = overview.find(item => item?.category === name);
    return {name, concern:numeric(source?.concern), worstConcern:numeric(source?.worstConcern), source:source ?? null};
  });
  const metrics = (Array.isArray(raw.scorecard) ? raw.scorecard : []).filter(item => item && typeof item === 'object').map(item => ({
    name:text(item.label) || text(item.metricName),
    value:item.measured === true ? numeric(item.averageValue) : null,
    concern:item.measured === true ? numeric(item.averageConcern) : null,
    direction:text(item.direction), source:item,
  }));
  const files = raw.files.filter(item => item && typeof item === 'object').map(item => ({
    path:text(item.file), concern:numeric(item.score), rating:text(item.rating),
    priority:numeric(item.priority), references:count(item.referencedByCount), source:item,
  })).sort((a,b) => (b.priority ?? -Infinity) - (a.priority ?? -Infinity));
  return {
    response, title:text(response.title), score:numeric(response.healthScore) ?? healthFromConcern(raw.score),
    rating:text(response.rating) || text(raw.rating), status:text(response.status),
    startedUtc:text(response.startedUtc), durationMs:numeric(response.durationMs),
    counts:{analyzed:count(response.analyzedFileCount) ?? count(raw.analyzedFileCount),
      skipped:count(response.skippedFileCount) ?? count(raw.skippedFileCount), ignored:count(response.ignoredFileCount)},
    categories, metrics, files,
  };
}

