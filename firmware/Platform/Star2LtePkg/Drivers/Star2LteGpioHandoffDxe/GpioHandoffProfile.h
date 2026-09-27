/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_GPIO_HANDOFF_PROFILE_H
#define STAR2LTE_GPIO_HANDOFF_PROFILE_H
#ifndef STAR2LTE_GPIO_HANDOFF_MODE
#define STAR2LTE_GPIO_HANDOFF_MODE 0
#endif
#if STAR2LTE_GPIO_HANDOFF_MODE != 0 && STAR2LTE_GPIO_HANDOFF_MODE != 3 && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error Supported policies are Off (0), source IRQ state (3), and masked input preparation (4)
#endif
#ifndef STAR2LTE_USB_HANDOFF_OBSERVER
#define STAR2LTE_USB_HANDOFF_OBSERVER 0
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER != 0 && STAR2LTE_USB_HANDOFF_OBSERVER != 1
#error USB handoff observation is either Off (0) or read-only (1)
#endif
#include "Usb2AccessProfile.h"
#ifndef STAR2LTE_HSI40_HANDOFF
#define STAR2LTE_HSI40_HANDOFF 0
#endif
#if STAR2LTE_HSI40_HANDOFF != 0 && STAR2LTE_HSI40_HANDOFF != 1
#error HSI2C40 handoff must be explicitly off or on
#endif
#if STAR2LTE_HSI40_HANDOFF && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error HSI2C40 handoff requires the qualified policy-four authority path
#endif
#ifndef STAR2LTE_HSI40_COLD_CENSUS
#define STAR2LTE_HSI40_COLD_CENSUS 0
#endif
#if STAR2LTE_HSI40_COLD_CENSUS != 0 && STAR2LTE_HSI40_COLD_CENSUS != 1
#error HSI2C40 cold census must be explicitly off or on
#endif
#if STAR2LTE_HSI40_COLD_CENSUS && !STAR2LTE_HSI40_HANDOFF
#error HSI2C40 cold census requires the gated HSI40 caller
#endif
#ifndef STAR2LTE_HSI40_COLD_TRIAL
#define STAR2LTE_HSI40_COLD_TRIAL 0
#endif
#if STAR2LTE_HSI40_COLD_TRIAL != 0 && STAR2LTE_HSI40_COLD_TRIAL != 1
#error HSI2C40 cold trial must be explicitly off or on
#endif
#if STAR2LTE_HSI40_COLD_TRIAL && (!STAR2LTE_HSI40_HANDOFF || STAR2LTE_HSI40_COLD_CENSUS)
#error HSI2C40 cold trial requires the gated caller and excludes cold census
#endif
#ifndef H40_COLD_RESET_ONLY
#define H40_COLD_RESET_ONLY 0
#endif
#if H40_COLD_RESET_ONLY != 0 && H40_COLD_RESET_ONLY != 1
#error HSI2C40 reset-only specialization must be explicitly off or on
#endif
#if H40_COLD_RESET_ONLY && !STAR2LTE_HSI40_COLD_TRIAL
#error HSI2C40 reset-only specialization requires the reversible cold trial
#endif
#ifndef STAR2LTE_UART1_COLD_CENSUS
#define STAR2LTE_UART1_COLD_CENSUS 0
#endif
#if STAR2LTE_UART1_COLD_CENSUS != 0 && STAR2LTE_UART1_COLD_CENSUS != 1
#error UART1 cold census must be explicitly off or on
#endif
#if STAR2LTE_UART1_COLD_CENSUS && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error UART1 cold census requires the qualified policy-four authority path
#endif
#endif
