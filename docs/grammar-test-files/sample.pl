use strict;
use warnings;

# Perl grammar sample
sub greet {
    my ($name) = @_;
    return "hello $name\n";
}

print greet("Volt");
