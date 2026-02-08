(function () {
    // 1. Inject Canvas
    const canvas = document.createElement('canvas');
    canvas.id = 'bg-canvas';
    // Style properties to ensure it sits in the background but over the body-color
    Object.assign(canvas.style, {
        position: 'fixed',
        top: '0',
        left: '0',
        width: '100%',
        height: '100%',
        zIndex: '0', // Content should be z-index 1 or higher
        pointerEvents: 'none'
    });
    document.body.prepend(canvas);

    const ctx = canvas.getContext('2d');
    let width, height;
    let particles = [];
    let mouse = { x: null, y: null };

    // Vibe Rails Palette
    const colors = [
        'rgba(91, 42, 134, 0.6)',   // #5b2a86 (Primary Purple)
        'rgba(154, 198, 197, 0.6)', // #9ac6c5 (Accent Teal)
        'rgba(119, 133, 172, 0.6)', // #7785ac (Secondary Blue)
        'rgba(255, 255, 255, 0.3)'  // Subtle White
    ];

    const config = {
        particleCount: 80, // Slightly less dense for a dashboard
        connectDistance: 120,
        mouseRepel: 150,
        speed: 0.4
    };

    function resize() {
        width = canvas.width = window.innerWidth;
        height = canvas.height = window.innerHeight;
    }

    window.addEventListener('resize', resize);
    resize();

    class Particle {
        constructor() {
            this.x = Math.random() * width;
            this.y = Math.random() * height;
            this.vx = (Math.random() - 0.5) * config.speed;
            this.vy = (Math.random() - 0.5) * config.speed;
            this.size = Math.random() * 2 + 0.5;
            this.color = colors[Math.floor(Math.random() * colors.length)];
        }

        update() {
            this.x += this.vx;
            this.y += this.vy;

            // Mouse interaction
            if (mouse.x != null) {
                let dx = mouse.x - this.x;
                let dy = mouse.y - this.y;
                let distance = Math.sqrt(dx * dx + dy * dy);

                if (distance < config.mouseRepel) {
                    const forceDirectionX = dx / distance;
                    const forceDirectionY = dy / distance;
                    const force = (config.mouseRepel - distance) / config.mouseRepel;
                    const directionX = forceDirectionX * force * 0.6;
                    const directionY = forceDirectionY * force * 0.6;

                    this.vx -= directionX;
                    this.vy -= directionY;
                }
            }

            // Friction
            this.vx *= 0.98;
            this.vy *= 0.98;

            // Min speed
            if (Math.abs(this.vx) < 0.1) this.vx += (Math.random() - 0.5) * 0.05;
            if (Math.abs(this.vy) < 0.1) this.vy += (Math.random() - 0.5) * 0.05;

            // Wrap
            if (this.x < 0) this.x = width;
            if (this.x > width) this.x = 0;
            if (this.y < 0) this.y = height;
            if (this.y > height) this.y = 0;
        }

        draw() {
            ctx.beginPath();
            ctx.arc(this.x, this.y, this.size, 0, Math.PI * 2);
            ctx.fillStyle = this.color;
            ctx.fill();
        }
    }

    function initParticles() {
        particles = [];
        for (let i = 0; i < config.particleCount; i++) {
            particles.push(new Particle());
        }
    }

    initParticles();

    function animate() {
        ctx.clearRect(0, 0, width, height);

        particles.forEach(p => {
            p.update();
            p.draw();
        });

        // Connections
        ctx.lineWidth = 0.5;
        for (let a = 0; a < particles.length; a++) {
            for (let b = a; b < particles.length; b++) {
                let dx = particles[a].x - particles[b].x;
                let dy = particles[a].y - particles[b].y;
                let distance = Math.sqrt(dx * dx + dy * dy);

                if (distance < config.connectDistance) {
                    // Gradient line opacity based on distance
                    const opacity = 1 - (distance / config.connectDistance);
                    ctx.strokeStyle = `rgba(154, 198, 197, ${opacity * 0.15})`; // Use accent color low opacity
                    ctx.beginPath();
                    ctx.moveTo(particles[a].x, particles[a].y);
                    ctx.lineTo(particles[b].x, particles[b].y);
                    ctx.stroke();
                }
            }
        }

        requestAnimationFrame(animate);
    }

    animate();

    window.addEventListener('mousemove', (e) => {
        mouse.x = e.clientX;
        mouse.y = e.clientY;
    });

    window.addEventListener('mouseleave', () => {
        mouse.x = null;
        mouse.y = null;
    });

})();
