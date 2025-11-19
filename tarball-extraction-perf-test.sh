#!/bin/bash

# Tarball Extraction Performance Test
# Measures extraction time for original vs deduplicated SDK tarballs

set -e

ORIGINAL_TARBALL="/repos/dotnet-sdk-10.0.100-linux-x64.tar.gz"
DEDUPLICATED_TARBALL="/repos/dotnet-sdk-10.0.100-linux-x64-deduplicated.tar.gz"
ITERATIONS=${1:-10}  # Default to 10 iterations, can be overridden
TEMP_BASE="/tmp/tarball-perf-test"

echo "========================================="
echo "Tarball Extraction Performance Test"
echo "========================================="
echo "Iterations: $ITERATIONS"
echo ""

# Verify tarballs exist
if [[ ! -f "$ORIGINAL_TARBALL" ]]; then
    echo "Error: Original tarball not found: $ORIGINAL_TARBALL"
    exit 1
fi

if [[ ! -f "$DEDUPLICATED_TARBALL" ]]; then
    echo "Error: Deduplicated tarball not found: $DEDUPLICATED_TARBALL"
    exit 1
fi

# Get file sizes
ORIGINAL_SIZE=$(stat -c%s "$ORIGINAL_TARBALL" 2>/dev/null || stat -f%z "$ORIGINAL_TARBALL")
DEDUP_SIZE=$(stat -c%s "$DEDUPLICATED_TARBALL" 2>/dev/null || stat -f%z "$DEDUPLICATED_TARBALL")
ORIGINAL_SIZE_MB=$(python3 -c "print(f'{$ORIGINAL_SIZE / 1024 / 1024:.2f}')")
DEDUP_SIZE_MB=$(python3 -c "print(f'{$DEDUP_SIZE / 1024 / 1024:.2f}')")

echo "Original tarball size: $ORIGINAL_SIZE_MB MB"
echo "Deduplicated tarball size: $DEDUP_SIZE_MB MB"
echo ""

# Function to extract tarball and measure time
extract_and_measure() {
    local tarball=$1
    local output_dir=$2

    # Measure extraction time
    local start=$(date +%s%N)
    tar -xzf "$tarball" -C "$output_dir" 2>/dev/null
    local end=$(date +%s%N)

    # Calculate elapsed time in seconds
    local elapsed_ns=$((end - start))
    local elapsed=$(python3 -c "print(f'{$elapsed_ns / 1000000000:.4f}')")
    echo "$elapsed"
}

# Test Original Tarball
echo "Testing ORIGINAL tarball ($ITERATIONS iterations)..."
original_times_file=$(mktemp)
for i in $(seq 1 $ITERATIONS); do
    test_dir="$TEMP_BASE/original-$i"
    mkdir -p "$test_dir"

    elapsed=$(extract_and_measure "$ORIGINAL_TARBALL" "$test_dir")
    echo "$elapsed" >> "$original_times_file"

    printf "  Iteration %2d: %.4f seconds\n" "$i" "$elapsed"

    # Cleanup
    rm -rf "$test_dir"
done

echo ""
echo "Testing DEDUPLICATED tarball ($ITERATIONS iterations)..."
dedup_times_file=$(mktemp)
for i in $(seq 1 $ITERATIONS); do
    test_dir="$TEMP_BASE/dedup-$i"
    mkdir -p "$test_dir"

    elapsed=$(extract_and_measure "$DEDUPLICATED_TARBALL" "$test_dir")
    echo "$elapsed" >> "$dedup_times_file"

    printf "  Iteration %2d: %.4f seconds\n" "$i" "$elapsed"

    # Cleanup
    rm -rf "$test_dir"
done

# Calculate statistics using Python
echo ""
echo "========================================="
echo "Results"
echo "========================================="
echo ""

# Calculate stats for original tarball
original_stats=$(python3 << EOF
import statistics

with open('$original_times_file') as f:
    times = [float(line.strip()) for line in f if line.strip()]

mean = statistics.mean(times)
median = statistics.median(times)
min_time = min(times)
max_time = max(times)
stddev = statistics.stdev(times) if len(times) > 1 else 0

print(f"{mean:.4f} {median:.4f} {min_time:.4f} {max_time:.4f} {stddev:.4f}")
EOF
)

# Calculate stats for deduplicated tarball
dedup_stats=$(python3 << EOF
import statistics

with open('$dedup_times_file') as f:
    times = [float(line.strip()) for line in f if line.strip()]

mean = statistics.mean(times)
median = statistics.median(times)
min_time = min(times)
max_time = max(times)
stddev = statistics.stdev(times) if len(times) > 1 else 0

print(f"{mean:.4f} {median:.4f} {min_time:.4f} {max_time:.4f} {stddev:.4f}")
EOF
)

read original_mean original_median original_min original_max original_stddev <<< "$original_stats"
read dedup_mean dedup_median dedup_min dedup_max dedup_stddev <<< "$dedup_stats"

echo "ORIGINAL TARBALL ($ORIGINAL_SIZE_MB MB):"
printf "  Mean:   %.4f seconds\n" "$original_mean"
printf "  Median: %.4f seconds\n" "$original_median"
printf "  Min:    %.4f seconds\n" "$original_min"
printf "  Max:    %.4f seconds\n" "$original_max"
printf "  StdDev: %.4f seconds\n" "$original_stddev"
echo ""

echo "DEDUPLICATED TARBALL ($DEDUP_SIZE_MB MB):"
printf "  Mean:   %.4f seconds\n" "$dedup_mean"
printf "  Median: %.4f seconds\n" "$dedup_median"
printf "  Min:    %.4f seconds\n" "$dedup_min"
printf "  Max:    %.4f seconds\n" "$dedup_max"
printf "  StdDev: %.4f seconds\n" "$dedup_stddev"
echo ""

# Calculate improvement
improvements=$(python3 << EOF
time_saved_mean = $original_mean - $dedup_mean
time_saved_median = $original_median - $dedup_median
percent_improvement_mean = (time_saved_mean / $original_mean) * 100
percent_improvement_median = (time_saved_median / $original_median) * 100

print(f"{time_saved_mean:.4f} {time_saved_median:.4f} {percent_improvement_mean:.2f} {percent_improvement_median:.2f}")
EOF
)

read time_saved_mean time_saved_median percent_improvement_mean percent_improvement_median <<< "$improvements"

echo "IMPROVEMENT:"
printf "  Time saved (mean):   %.4f seconds (%.2f%% faster)\n" "$time_saved_mean" "$percent_improvement_mean"
printf "  Time saved (median): %.4f seconds (%.2f%% faster)\n" "$time_saved_median" "$percent_improvement_median"
echo ""

# Cleanup temp files
rm -f "$original_times_file" "$dedup_times_file"
rmdir "$TEMP_BASE" 2>/dev/null || true

echo "========================================="
echo "Test completed successfully"
echo "========================================="
