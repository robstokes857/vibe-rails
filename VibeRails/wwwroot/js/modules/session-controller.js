export class SessionController {
    constructor(app) {
        this.app = app;
    }

    async loadSessions() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('sessions-template');
        const root = fragment.querySelector('[data-view="sessions"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());

            const sessionsList = root.querySelector('[data-sessions-list]');
            if (sessionsList) {
                sessionsList.innerHTML = '<div class="text-center"><div class="spinner-border text-primary"></div><p class="mt-2">Loading sessions...</p></div>';
            }
        }

        content.appendChild(fragment);
        await this.fetchAndRenderSessions();
    }

    async fetchAndRenderSessions() {
        const sessionsList = document.querySelector('[data-sessions-list]');
        if (!sessionsList) return;

        try {
            const sessions = await this.app.apiCall('/api/v1/sessions/recent?limit=10', 'GET');
            this.populateSessionsList(sessionsList, sessions);
        } catch (error) {
            sessionsList.innerHTML = `<p class="text-danger text-center">Failed to load sessions: ${error.message}</p>`;
        }
    }

    populateSessionsList(container, sessions) {
        container.innerHTML = '';
        if (!sessions || sessions.length === 0) {
            container.innerHTML = '<p class="text-muted text-center">No sessions found. Launch a CLI to create a session.</p>';
            return;
        }

        const template = document.getElementById('session-item-template');
        if (!template) return;

        const fragment = document.createDocumentFragment();

        sessions.forEach(session => {
            const node = template.content.cloneNode(true);
            const brand = this.app.getCliBrand(session.cli);
            const started = new Date(session.startedUTC).toLocaleString();
            const isRunning = !session.endedUTC;

            const logo = node.querySelector('[data-session-logo]');
            if (logo && brand.logo) {
                logo.src = brand.logo;
                logo.alt = `${brand.label} logo`;
            } else if (logo) {
                if (logo.parentElement?.classList.contains('project-logo-wrapper')) {
                    logo.parentElement.style.display = 'none';
                }
                logo.style.display = 'none';
            }

            const badge = node.querySelector('[data-session-badge]');
            if (badge) {
                badge.textContent = session.cli;
                if (brand.className) badge.classList.add(brand.className);
            }

            const time = node.querySelector('[data-session-time]');
            if (time) time.textContent = started;

            const workdir = node.querySelector('[data-session-workdir]');
            if (workdir) workdir.textContent = session.workingDirectory;

            const envContainer = node.querySelector('[data-session-env-container]');
            if (envContainer && session.environmentName) {
                envContainer.textContent = `Env: ${session.environmentName}`;
            }

            const statusBadge = node.querySelector('[data-session-status]');
            if (statusBadge) {
                if (isRunning) {
                     statusBadge.remove();
                } else {
                     statusBadge.textContent = `Exit: ${session.exitCode ?? 'N/A'}`;
                     statusBadge.classList.add('bg-secondary');
                }
            }

            fragment.appendChild(node);
        });

        const listGroup = document.createElement('div');
        listGroup.className = 'list-group';
        listGroup.appendChild(fragment);
        container.appendChild(listGroup);
    }
}
