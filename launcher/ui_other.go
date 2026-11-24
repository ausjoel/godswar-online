//go:build !darwin

package main

import (
	"fmt"
	"os"
)

func notify(c *Config, msg string) {
	if c != nil && c.Status != nil {
		c.Status(msg)
		return
	}
	fmt.Println(msg)
}

// failBox (Windows) shows the error in a message box, since the GUI build has no console.
var failBox func(string)

func fail(c *Config, msg string) {
	fmt.Fprintln(os.Stderr, msg)
	if failBox != nil {
		failBox(msg)
		os.Exit(1)
	}
	fmt.Println("Press Enter to close.")
	fmt.Scanln()
	os.Exit(1)
}

func info(c *Config, msg string) {
	if c != nil && c.Status != nil {
		c.Status(msg)
		return
	}
	fmt.Println(msg)
}

func rosettaProblem() string { return "" }

func optionHeld() bool { return false }

func askEngine(current string) string { return current }
