#!/usr/bin/env python
"""A longer, GPU-heavy workload: train a ResNet from scratch on synthetic-but-learnable data.

This exists to profile the wrapper against a *sustained* process (tens of seconds) that streams
progress the whole time -- a very different shape from the one-shot classifier. It lets us watch:
  * real-time streaming (per-step / per-epoch lines must arrive live, not dumped at the end),
  * steady-state GPU throughput (images/sec) and peak GPU memory,
  * how the fixed ~seconds of Python/torch startup shrink to noise as the run gets longer.

The data is random images, but each image's label comes from a FIXED random linear "teacher", so
the task is genuinely learnable: the loss goes down and accuracy climbs above chance. No dataset
download, fully deterministic given --seed.

Contract (same as the rest of the repo): stdout = final JSON only; stderr = live progress.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import sys
import time


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def emit(payload: dict) -> None:
    print(json.dumps(payload, indent=2), flush=True)


def cmd_train(args: argparse.Namespace) -> int:
    import_started = time.perf_counter()
    try:
        import torch  # noqa: PLC0415
        import torch.nn as nn  # noqa: PLC0415
        from torchvision import models  # noqa: PLC0415
    except ImportError as exc:
        log(f"[train] ERROR: PyTorch/torchvision not installed: {exc}")
        return 3
    torch_import_seconds = time.perf_counter() - import_started

    try:
        with contextlib.redirect_stdout(sys.stderr):
            torch.manual_seed(args.seed)
            device = "cuda" if torch.cuda.is_available() else "cpu"
            log(f"[train] device={device} model={args.model} img={args.image_size} "
                f"batch={args.batch_size} epochs={args.epochs} steps/epoch={args.steps_per_epoch}")

            setup_started = time.perf_counter()
            factory = {"resnet18": models.resnet18, "resnet50": models.resnet50}[args.model]
            model = factory(weights=None, num_classes=args.num_classes).to(device)

            # A fixed random linear "teacher" over raw pixels defines the labels. Frozen (no grad).
            features = 3 * args.image_size * args.image_size
            teacher = nn.Linear(features, args.num_classes, bias=False).to(device)
            for p in teacher.parameters():
                p.requires_grad_(False)

            optimizer = torch.optim.Adam(model.parameters(), lr=args.lr)
            criterion = nn.CrossEntropyLoss()
            if device == "cuda":
                torch.cuda.reset_peak_memory_stats()
            setup_seconds = time.perf_counter() - setup_started

            def make_batch():
                x = torch.randn(args.batch_size, 3, args.image_size, args.image_size, device=device)
                with torch.no_grad():
                    y = teacher(x.flatten(1)).argmax(dim=1)
                return x, y

            # Warm up one step (kernels/cuDNN autotune) so per-epoch throughput is honest.
            x, y = make_batch()
            optimizer.zero_grad(set_to_none=True)
            loss = criterion(model(x), y)
            loss.backward()
            optimizer.step()
            if device == "cuda":
                torch.cuda.synchronize()

            per_epoch = []
            train_started = time.perf_counter()
            model.train()

            for epoch in range(1, args.epochs + 1):
                epoch_started = time.perf_counter()
                running_loss = 0.0
                running_correct = 0
                running_total = 0

                for step in range(1, args.steps_per_epoch + 1):
                    x, y = make_batch()
                    optimizer.zero_grad(set_to_none=True)
                    logits = model(x)
                    loss = criterion(logits, y)
                    loss.backward()
                    optimizer.step()

                    running_loss += loss.item()
                    running_correct += (logits.argmax(dim=1) == y).sum().item()
                    running_total += y.numel()

                    if step % args.log_every == 0 or step == args.steps_per_epoch:
                        log(f"[train] epoch {epoch}/{args.epochs} step {step}/{args.steps_per_epoch} "
                            f"loss={running_loss / step:.4f} acc={running_correct / running_total:.3f}")

                if device == "cuda":
                    torch.cuda.synchronize()
                epoch_seconds = time.perf_counter() - epoch_started
                images = args.batch_size * args.steps_per_epoch
                per_epoch.append({
                    "epoch": epoch,
                    "mean_loss": round(running_loss / args.steps_per_epoch, 6),
                    "accuracy": round(running_correct / running_total, 6),
                    "seconds": round(epoch_seconds, 4),
                    "images_per_sec": round(images / epoch_seconds, 1),
                })
                last = per_epoch[-1]
                log(f"[train] epoch {epoch}/{args.epochs} DONE loss={last['mean_loss']:.4f} "
                    f"acc={last['accuracy']:.3f} {last['images_per_sec']:.0f} img/s "
                    f"({last['seconds']:.2f}s)")

            train_seconds = time.perf_counter() - train_started
            total_images = args.batch_size * args.steps_per_epoch * args.epochs
            peak_mem_mb = (torch.cuda.max_memory_allocated() / (1024 * 1024)) if device == "cuda" else 0.0

            result = {
                "ok": True,
                "model": args.model,
                "device": device,
                "cuda_available": bool(torch.cuda.is_available()),
                "gpu_name": torch.cuda.get_device_name(0) if device == "cuda" else None,
                "epochs": args.epochs,
                "steps_per_epoch": args.steps_per_epoch,
                "batch_size": args.batch_size,
                "image_size": args.image_size,
                "torch_import_seconds": round(torch_import_seconds, 6),
                "setup_seconds": round(setup_seconds, 6),
                "train_seconds": round(train_seconds, 6),
                "total_images": total_images,
                "mean_images_per_sec": round(total_images / train_seconds, 1),
                "final_loss": per_epoch[-1]["mean_loss"],
                "final_accuracy": per_epoch[-1]["accuracy"],
                "peak_gpu_memory_mb": round(peak_mem_mb, 1),
                "per_epoch": per_epoch,
            }
            log(f"[train] finished: {result['mean_images_per_sec']:.0f} img/s mean, "
                f"final acc {result['final_accuracy']:.3f}, peak GPU {result['peak_gpu_memory_mb']:.0f} MB")

        emit(result)
        return 0
    except Exception as exc:  # noqa: BLE001
        log(f"[train] ERROR: {type(exc).__name__}: {exc}")
        return 4


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Train a ResNet on synthetic learnable data (GPU workload).")
    parser.add_argument("--model", default="resnet18", choices=["resnet18", "resnet50"])
    parser.add_argument("--epochs", type=int, default=10)
    parser.add_argument("--steps-per-epoch", type=int, default=50)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--image-size", type=int, default=224)
    parser.add_argument("--num-classes", type=int, default=10)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--log-every", type=int, default=10)
    parser.add_argument("--seed", type=int, default=0)
    parser.set_defaults(func=cmd_train)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
