"""Small statistics helpers (no numpy dependency)."""

from __future__ import annotations

import math
from statistics import mean, stdev

Z = 1.96

# First-party Claude API prices, USD per million tokens (input, output).
# Cache writes bill at 1.25x input, cache reads at 0.1x input.
PRICES = {
    "claude-fable-5-1": (10.0, 50.0),
    "claude-fable-5": (10.0, 50.0),
    "claude-opus-5-5": (4.0, 20.0),
    "claude-opus-5": (5.0, 25.0),
    "claude-opus-4-8": (5.0, 25.0),
    "claude-opus-4-7": (5.0, 25.0),
    "claude-opus-4-6": (5.0, 25.0),
    "claude-sonnet-5-5": (2.0, 10.0),
    "claude-sonnet-5": (2.0, 10.0),
    "claude-sonnet-4-6": (3.0, 15.0),
    "claude-haiku-4-5": (1.0, 5.0),
}


def price_for(model: str | None) -> tuple[float, float] | None:
    if not model:
        return None
    for key in sorted(PRICES, key=len, reverse=True):
        if model == key or model.startswith(key + "-"):
            return PRICES[key]
    return None


def cost_usd(model: str | None, usage: dict | None) -> float | None:
    p = price_for(model)
    if p is None or not usage:
        return None
    pin, pout = p
    return (
        usage.get("input_tokens", 0) * pin
        + usage.get("cache_creation_input_tokens", 0) * pin * 1.25
        + usage.get("cache_read_input_tokens", 0) * pin * 0.1
        + usage.get("output_tokens", 0) * pout
    ) / 1_000_000


def wilson(successes: float, n: int) -> tuple[float, float]:
    if n == 0:
        return (0.0, 0.0)
    p = successes / n
    denom = 1 + Z**2 / n
    centre = (p + Z**2 / (2 * n)) / denom
    half = Z * math.sqrt(p * (1 - p) / n + Z**2 / (4 * n * n)) / denom
    return (max(0.0, centre - half), min(1.0, centre + half))


def mean_ci(values: list[float]) -> tuple[float, float, float]:
    """Mean and normal-approx 95% CI half-width."""
    if not values:
        return (float("nan"), float("nan"), float("nan"))
    m = mean(values)
    if len(values) < 2:
        return (m, m, m)
    half = Z * stdev(values) / math.sqrt(len(values))
    return (m, m - half, m + half)


def paired_diff(a: list[float], b: list[float]) -> tuple[float, float, float]:
    """Mean of (b - a) over paired per-case scores, with a 95% CI."""
    return mean_ci([y - x for x, y in zip(a, b)])
