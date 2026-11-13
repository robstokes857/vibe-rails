(function () {
    const performanceMode = window.VibeRailsPerformance;
    const canvas = document.createElement('canvas');
    canvas.id = 'bg-canvas';
    Object.assign(canvas.style, {
        position: 'fixed',
        top: '0',
        left: '0',
        width: '100%',
        height: '100%',
        zIndex: '-1',
        pointerEvents: 'none',
        opacity: '0.8',
        display: performanceMode?.isEnabled?.() ? 'none' : ''
    });
    document.body.prepend(canvas);

    const ctx = canvas.getContext('2d');
    let width, height;
    let particles = [];
    const mouse = { x: null, y: null };

    // Get theme colors but keep them very subtle
    function getColors() {
        const style = getComputedStyle(document.documentElement);
        const primary = style.getPropertyValue('--color-primary').trim() || '#3b82f6';
        const secondary = style.getPropertyValue('--color-secondary').trim() || '#64748b';
        
        // Return colors with low opacity for the "little balls"
        return [
            primary + '30',
            secondary + '24',
            'rgba(255, 255, 255, 0.08)'
        ];
    }

    const config = {
        count: 82,
        speed: 0.12,
        maxSpeed: 0.18,
        connectDist: 86,
        mouseDist: 115,
        mouseForce: 0.00018,
        lineOpacity: 0.05
    };

    class Particle {
        constructor() {
            this.init();
        }

        init() {
            this.x = Math.random() * width;
            this.y = Math.random() * height;
            this.vx = (Math.random() - 0.5) * config.speed;
            this.vy = (Math.random() - 0.5) * config.speed;
            this.radius = Math.random() * 1.2 + 0.9;
            const colors = getColors();
            this.color = colors[Math.floor(Math.random() * colors.length)];
        }

        update() {
            this.x += this.vx;
            this.y += this.vy;

            if (this.x < 0 || this.x > width) this.vx *= -1;
            if (this.y < 0 || this.y > height) this.vy *= -1;

            if (mouse.x !== null) {
                const dx = mouse.x - this.x;
                const dy = mouse.y - this.y;
                const dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < config.mouseDist) {
                    this.vx -= dx * config.mouseForce;
                    this.vy -= dy * config.mouseForce;
                }
            }

            const speed = Math.sqrt(this.vx * this.vx + this.vy * this.vy);
            if (speed > config.maxSpeed) {
                const scale = config.maxSpeed / speed;
                this.vx *= scale;
                this.vy *= scale;
            }
        }

        draw() {
            ctx.beginPath();
            ctx.arc(this.x, this.y, this.radius, 0, Math.PI * 2);
            ctx.fillStyle = this.color;
            ctx.fill();
        }
    }

    function resize() {
        width = canvas.width = window.innerWidth;
        height = canvas.height = window.innerHeight;
    }

    function isBackgroundDisabled() {
        return performanceMode?.isEnabled?.() === true;
    }

    // VIBE-321: a decorative background does not need the display's refresh rate. Draw about 30 times
    // a second and advance the particles by the 60 Hz steps that elapsed, so they keep their speed.
    const FRAME_INTERVAL_MS = 1000 / 30;
    const STEP_MS = 1000 / 60;
    const MAX_STEPS = 4;

    let rafId = null;
    let isAnimating = false;
    let lastFrameAt = null;

    // The terminal focus view covers the canvas with an opaque panel; only a few pixels of gap show it.
    function isCoveredByView() {
        return document.documentElement.classList.contains('vb-terminal-focus-active');
    }

    function renderFrame(steps = 1) {
        ctx.clearRect(0, 0, width, height);

        for (let i = 0; i < particles.length; i++) {
            const p1 = particles[i];
            for (let step = 0; step < steps; step++) {
                p1.update();
            }
            p1.draw();

            for (let j = i + 1; j < particles.length; j++) {
                const p2 = particles[j];
                const dx = p1.x - p2.x;
                const dy = p1.y - p2.y;
                const dist = Math.sqrt(dx * dx + dy * dy);

                if (dist < config.connectDist) {
                    ctx.beginPath();
                    ctx.strokeStyle = `rgba(148, 163, 184, ${config.lineOpacity * (1 - dist / config.connectDist)})`;
                    ctx.lineWidth = 0.5;
                    ctx.moveTo(p1.x, p1.y);
                    ctx.lineTo(p2.x, p2.y);
                    ctx.stroke();
                }
            }
        }
    }

    function animate(timestamp) {
        if (!isAnimating) {
            return;
        }

        if (lastFrameAt === null) {
            lastFrameAt = timestamp;
            renderFrame();
        } else if (timestamp - lastFrameAt >= FRAME_INTERVAL_MS - 1) {
            const steps = Math.min(MAX_STEPS, Math.max(1, Math.round((timestamp - lastFrameAt) / STEP_MS)));
            lastFrameAt = timestamp;
            renderFrame(steps);
        }
        rafId = requestAnimationFrame(animate);
    }

    function startAnimation() {
        if (isAnimating || isBackgroundDisabled() || document.hidden || isCoveredByView()) {
            return;
        }

        isAnimating = true;
        lastFrameAt = null;
        rafId = requestAnimationFrame(animate);
    }

    function stopAnimation() {
        if (rafId !== null) {
            cancelAnimationFrame(rafId);
            rafId = null;
        }
        isAnimating = false;
    }

    function updateAnimationState() {
        canvas.style.display = isBackgroundDisabled() ? 'none' : '';

        // A covered canvas keeps its last frame and resumes when the view changes.
        if (isBackgroundDisabled() || document.hidden || isCoveredByView()) {
            stopAnimation();
        } else {
            startAnimation();
        }
    }

    window.addEventListener('resize', () => {
        resize();
        particles = Array.from({ length: config.count }, () => new Particle());
        if (!isBackgroundDisabled()) {
            renderFrame();
        }
    });

    window.addEventListener('mousemove', (e) => {
        mouse.x = e.clientX;
        mouse.y = e.clientY;
    });

    window.addEventListener('mouseleave', () => {
        mouse.x = null;
        mouse.y = null;
    });

    document.addEventListener('visibilitychange', updateAnimationState);
    performanceMode?.subscribe?.(() => updateAnimationState());
    // Views toggle their layout classes on <html> (app.js applyViewLayoutState).
    new MutationObserver(updateAnimationState).observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['class']
    });

    resize();
    particles = Array.from({ length: config.count }, () => new Particle());

    updateAnimationState();
})();
