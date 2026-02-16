const ICON_BASE = 'assets/img/filetypes/';

const FILE_TYPE_MAP = {
    // Documents
    pdf: { name: 'PDF Document', icon: 'pdf.svg' },
    doc: { name: 'Word Document', icon: 'word.svg' },
    docx: { name: 'Word Document', icon: 'word.svg' },
    txt: { name: 'Text File', icon: 'text.svg' },
    rtf: { name: 'Rich Text', icon: 'text.svg' },

    // Web / markup / data
    md: { name: 'Markdown', icon: 'markdown.svg' },
    markdown: { name: 'Markdown', icon: 'markdown.svg' },
    html: { name: 'HTML', icon: 'html.svg' },
    htm: { name: 'HTML', icon: 'html.svg' },
    css: { name: 'CSS', icon: 'css.svg' },
    xml: { name: 'XML', icon: 'xml.svg' },
    json: { name: 'JSON', icon: 'json.svg' },
    yaml: { name: 'YAML', icon: 'yaml.svg' },
    yml: { name: 'YAML', icon: 'yaml.svg' },
    toml: { name: 'TOML', icon: 'data.svg' },
    ini: { name: 'INI Config', icon: 'config.svg' },
    csv: { name: 'CSV', icon: 'data.svg' },
    sql: { name: 'SQL', icon: 'sql.svg' },

    // C-family
    c: { name: 'C Source', icon: 'c.svg' },
    h: { name: 'C Header', icon: 'c.svg' },
    cs: { name: 'C#', icon: 'csharp.svg' },
    cpp: { name: 'C++', icon: 'cplusplus.svg' },
    cxx: { name: 'C++', icon: 'cplusplus.svg' },
    cc: { name: 'C++', icon: 'cplusplus.svg' },
    hpp: { name: 'C++ Header', icon: 'cplusplus.svg' },
    hxx: { name: 'C++ Header', icon: 'cplusplus.svg' },

    // Common languages
    rs: { name: 'Rust', icon: 'rust.svg' },
    go: { name: 'Go', icon: 'go.svg' },
    rb: { name: 'Ruby', icon: 'ruby.svg' },
    php: { name: 'PHP', icon: 'php.svg' },
    pl: { name: 'Perl', icon: 'perl.svg' },
    pm: { name: 'Perl Module', icon: 'perl.svg' },
    py: { name: 'Python', icon: 'python.svg' },
    java: { name: 'Java', icon: 'java.svg' },
    kt: { name: 'Kotlin', icon: 'kotlin.svg' },
    kts: { name: 'Kotlin Script', icon: 'kotlin.svg' },
    swift: { name: 'Swift', icon: 'swift.svg' },
    js: { name: 'JavaScript', icon: 'javascript.svg' },
    jsx: { name: 'JavaScript JSX', icon: 'javascript.svg' },
    mjs: { name: 'JavaScript Module', icon: 'javascript.svg' },
    ts: { name: 'TypeScript', icon: 'typescript.svg' },
    tsx: { name: 'TypeScript TSX', icon: 'typescript.svg' },

    // Shells / scripts
    sh: { name: 'Shell Script', icon: 'bash.svg' },
    bash: { name: 'Bash Script', icon: 'bash.svg' },
    zsh: { name: 'Zsh Script', icon: 'bash.svg' },
    ps1: { name: 'PowerShell', icon: 'powershell.svg' },
    psm1: { name: 'PowerShell Module', icon: 'powershell.svg' },
    psd1: { name: 'PowerShell Data', icon: 'powershell.svg' },
    bat: { name: 'Batch Script', icon: 'bash.svg' },
    cmd: { name: 'Command Script', icon: 'bash.svg' },

    // Extras
    dockerfile: { name: 'Dockerfile', icon: 'config.svg' },
    env: { name: 'Environment File', icon: 'config.svg' },
    gitignore: { name: 'Git Ignore', icon: 'config.svg' },
    log: { name: 'Log File', icon: 'text.svg' },
    zip: { name: 'Archive', icon: 'archive.svg' },
    rar: { name: 'Archive', icon: 'archive.svg' },
    '7z': { name: 'Archive', icon: 'archive.svg' },
    tar: { name: 'Archive', icon: 'archive.svg' },
    gz: { name: 'Archive', icon: 'archive.svg' },
    png: { name: 'Image', icon: 'image.svg' },
    jpg: { name: 'Image', icon: 'image.svg' },
    jpeg: { name: 'Image', icon: 'image.svg' },
    gif: { name: 'Image', icon: 'image.svg' },
    svg: { name: 'SVG Image', icon: 'image.svg' }
};

const DEFAULT_TYPE = { name: 'File', icon: 'file.svg' };

function extensionFromFileName(fileName) {
    const base = (fileName || '').split(/[\\/]/).pop()?.toLowerCase() || '';
    if (!base) return '';

    if (base === 'dockerfile') return 'dockerfile';
    if (base === '.env') return 'env';
    if (base === '.gitignore') return 'gitignore';

    const dotIndex = base.lastIndexOf('.');
    if (dotIndex <= 0 || dotIndex === base.length - 1) return '';
    return base.substring(dotIndex + 1);
}

export function getFileTypeVisual(fileName) {
    const ext = extensionFromFileName(fileName);
    const match = FILE_TYPE_MAP[ext] || DEFAULT_TYPE;
    return {
        name: match.name,
        iconPath: `${ICON_BASE}${match.icon}`
    };
}
