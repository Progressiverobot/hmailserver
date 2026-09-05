create table hm_archiveindex
(
	archiveid bigserial not null primary key,
	archivetime timestamp not null,
	archivedomain varchar(255) not null,
	archivemailbox varchar(255) not null,
	archivedirection int not null,
	archivesender varchar(255) not null,
	archiverecipients varchar(1000) not null,
	archivesubject varchar(255) not null,
	archivemessageid varchar(255) not null,
	archivepath varchar(1000) not null,
	archivesize bigint not null,
	archivehold int not null
);
CREATE INDEX idx_hm_archiveindex_domain_time ON hm_archiveindex (archivedomain, archivetime);
CREATE INDEX idx_hm_archiveindex_path ON hm_archiveindex (archivepath);

update hm_dbversion set value = 6029;
