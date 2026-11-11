SpecMES is a small Manufacturing Execution System that intends to work on a local PostgreSQL server, eliminating the need for cloud-based operations.
Written in C#, SpecMES consists of two primary components: a production routing and order generation module, and a client-side application accessible at production workstations.
The system is designed to work with serialized numbers (not batch production), allowing for detailed tracking of which operator performed specific tasks and with which tools. 
This ensures precise traceability and accountability in the production process.
Additionally, SpecMES supports plugins for certain electrical measurement devices, such as those using the AGILENT protocol, to read voltage/current values from specific tests or production steps. 
This feature enhances the system’s capability to integrate with various testing equipment, providing comprehensive data collection and analysis.
SpecMES aims to provide a robust, locally-hosted solution for manufacturing execution, focusing on detailed tracking and integration with production tools and measurement devices.
Traceability is a critical feature of SpecMES, often required by products and ISO standards that govern the production of reliability-sensitive items, such as ISO 22163 or IATF 16949.
The system is designed to meet part of the manufacturing stage requirements outlined in these standards. By ensuring detailed tracking of serialized components and their associated production data,
SpecMES helps manufacturers comply with stringent traceability requirements, thereby supporting quality assurance and regulatory compliance.

With kind regards,
Chief Innovator, Paweł