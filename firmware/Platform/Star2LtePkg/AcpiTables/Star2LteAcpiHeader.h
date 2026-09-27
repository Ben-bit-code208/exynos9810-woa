/** @file
  Shared ACPI definitions for Star2LtePkg (Exynos 9810 / Galaxy S9+).

  Provides the OEM identity and a header-initialization macro reused by every
  static ACPI table (.aslc) in this directory.
**/

#ifndef STAR2LTE_ACPI_HEADER_H_
#define STAR2LTE_ACPI_HEADER_H_

#include <IndustryStandard/Acpi.h>

//
// OEM identity. 6-byte OEM ID, 8-byte OEM Table ID.
//
#define EFI_ACPI_OEM_ID            {'E', 'X', 'Y', 'N', 'O', 'S'}
#define EFI_ACPI_OEM_TABLE_ID      SIGNATURE_64 ('S','T','A','R','2','L','T','E')
#define EFI_ACPI_OEM_REVISION      0x00000001
#define EFI_ACPI_CREATOR_ID        SIGNATURE_32 ('W','O','A',' ')
#define EFI_ACPI_CREATOR_REVISION  0x00000001

//
// A NULL Generic Address Structure (used to zero out unused FADT registers).
//
#define NULL_GAS  {EFI_ACPI_6_3_SYSTEM_MEMORY, 0, 0, EFI_ACPI_6_3_UNDEFINED, 0}

//
// Standard ACPI table header initializer. 'Signature' and 'Type' (the C struct
// type) and 'Revision' are table-specific; everything else is platform-wide.
//
#define ACPI_HEADER(Signature, Type, Revision) {                  \
    Signature,                       /* UINT32  Signature */       \
    sizeof (Type),                   /* UINT32  Length */          \
    Revision,                        /* UINT8   Revision */        \
    0,                               /* UINT8   Checksum (fixed up by installer) */ \
    EFI_ACPI_OEM_ID,                 /* UINT8   OemId[6] */        \
    EFI_ACPI_OEM_TABLE_ID,           /* UINT64  OemTableId */      \
    EFI_ACPI_OEM_REVISION,           /* UINT32  OemRevision */     \
    EFI_ACPI_CREATOR_ID,             /* UINT32  CreatorId */       \
    EFI_ACPI_CREATOR_REVISION        /* UINT32  CreatorRevision */ \
  }

#endif // STAR2LTE_ACPI_HEADER_H_
