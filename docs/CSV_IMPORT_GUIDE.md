# Batch Pick CSV Import Guide

## Overview
The **Batch Pick** tab now supports CSV import with **automatic label-to-address mapping**. This means you can create batch picking lists using light module labels (e.g., "Shelf-A1") instead of numeric addresses, and the system automatically maps them to their corresponding addresses stored in the database.

## CSV Formats

### Format 1: Label-Based (NEW - Recommended)
When the first column contains a text label, the system automatically looks it up in the database and maps it to the module address.

**Format:** `label,quantity,color`

```csv
label,quantity,color
Shelf-A1,5,GREEN
Shelf-A2,3,RED
Shelf-B1,10,BLUE
Warehouse-Zone-1,2,YELLOW
```

**Advantages:**
- More human-readable and intuitive
- Easier to maintain (no need to remember numeric addresses)
- Reduces picking errors by using descriptive location names
- Automatic database lookup

### Format 2: Address-Based (Legacy - Still Supported)
For backward compatibility, you can still use numeric module addresses directly.

**Format:** `address,quantity,color,label`

```csv
address,quantity,color,label
0001,5,GREEN,SKU-A001
0002,3,RED,SKU-B002
0003,10,BLUE,SKU-C003
0042,2,YELLOW,SKU-D004
```

**Note:** Addresses must be numeric and will be zero-padded to 4 digits (e.g., `1` → `0001`)

## How It Works

### Import Process
1. Click the **"Import CSV"** button in the Batch Pick tab
2. Select your CSV file
3. The system will:
   - Parse each row
   - Detect the format (label-based or address-based)
   - For labels: Look up the label in the devices database
   - For addresses: Use them directly
   - Validate colors and quantities
   - Add valid items to the batch builder

### Error Handling
- **Unmapped Labels:** If a label doesn't exist in the database, it will be skipped with a warning. You'll see a message like: "Imported 3 item(s) • 1 skipped (unmapped: Shelf-Unknown)"
- **Invalid Colors:** Defaults to GREEN if an unrecognized color is provided
- **Invalid Quantities:** Defaults to 1, clamped between 1-99999
- **Malformed Rows:** Skipped silently

## Supported Colors
- `OFF`
- `RED`
- `GREEN`
- `BLUE`
- `YELLOW`
- `CYAN`
- `MAGENTA`
- `WHITE`

## LED Modes
CSV imports default to `ON` (steady). To use other modes:
1. Import the CSV to add items to the batch
2. Click on individual items in the batch table to edit LED mode if needed
3. Or add items manually using the form

## Best Practices

### For Label-Based Imports
1. **Know Your Labels:** First, go to the **Devices** tab to see the registered label names
2. **Use Exact Case:** Label lookup is case-insensitive, but it's good practice to be consistent
3. **No Header Row:** Start with the first data row; don't include headers like "label,quantity,color"
4. **Comment Rows:** Prefix lines with `#` to add comments (automatically skipped)

### For Address-Based Imports
1. **Use 4-Digit Format:** Zero-pad addresses (e.g., `0001` instead of `1`)
2. **Verify Addresses:** Check the Devices tab to confirm module addresses
3. **Optional Label Field:** The 4th column (label/SKU) is optional for reference

## Example Workflows

### Workflow 1: Pick by Location (Label-Based)
```csv
Front-Shelf-1,8,GREEN
Front-Shelf-2,5,RED
Back-Aisle-A,12,BLUE
Back-Aisle-B,3,YELLOW
```

### Workflow 2: Pick by Address (Legacy)
```csv
0001,8,GREEN,Shelf-1
0002,5,RED,Shelf-2
0003,12,BLUE,Aisle-A
0004,3,YELLOW,Aisle-B
```

### Workflow 3: Mixed (Not Recommended)
The first column type determines the format for the entire file. Don't mix labels and addresses in one import—process separately.

## Troubleshooting

### Items Not Importing
- **Check Label Names:** Go to Devices tab and verify exact spelling
- **Check CSV Format:** Ensure commas are used as separators
- **Check for Spaces:** Label matching is exact; trim whitespace in your CSV

### Wrong Address Mapped
- Verify the label in the Devices tab is spelled correctly
- Some labels might be similar; check the devices list carefully
- If unsure, use address-based format instead

### Import Shows Warnings
- Unmapped labels are listed in the warning message
- Fix the CSV and import again
- Already-imported valid items remain in the batch

## Sample CSV Download
Click the **"⬇ Sample CSV"** button to download a template based on your current registered devices. This template will include:
1. Your actual device labels (if available)
2. Examples using both formats
3. Comments explaining the format

Use this as a starting point for your picking lists.
