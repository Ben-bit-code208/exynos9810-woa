/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include <stddef.h>

void *memcpy(void *destination,const void *source,size_t bytes)
{
    unsigned char *d=destination;const unsigned char *s=source;
    while(bytes--)*d++=*s++;
    return destination;
}
void *memset(void *destination,int value,size_t bytes)
{
    unsigned char *d=destination;
    while(bytes--)*d++=(unsigned char)value;
    return destination;
}
int memcmp(const void *left,const void *right,size_t bytes)
{
    const unsigned char *a=left,*b=right;
    while(bytes--){if(*a!=*b)return (int)*a-(int)*b;++a;++b;}
    return 0;
}
